using Vortice.MediaFoundation;

namespace Beamcast.Codec.Gpu;

/// <summary>Discovers Media Foundation transforms and remembers what this machine can do.</summary>
public static class MfCodecs
{
    private const uint FlagSync = 0x1;
    private const uint FlagAsync = 0x2;
    private const uint FlagHardware = 0x4;
    private const uint FlagSortAndFilter = 0x40;

    private static readonly Dictionary<VideoCodec, bool> EncoderCache = new();
    private static readonly Dictionary<VideoCodec, bool> DecoderCache = new();
    private static readonly Dictionary<VideoCodec, bool> ProbeCache = new();

    public static Guid Subtype(VideoCodec codec) =>
        codec switch
        {
            VideoCodec.H264 => VideoFormatGuids.H264,
            VideoCodec.Hevc => VideoFormatGuids.Hevc,
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };

    /// <summary>Activates the best hardware encoder for the codec, or null when there is none.</summary>
    public static IMFTransform? CreateHardwareEncoder(VideoCodec codec, out string name)
    {
        name = string.Empty;
        GpuDevice.EnsureMediaFoundation();
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Subtype(codec) };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, FlagHardware | FlagAsync | FlagSortAndFilter, null, output);
        foreach (var activate in activates)
        {
            try
            {
                var transform = activate.ActivateObject<IMFTransform>();
                name = SafeTry.Run(() => activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)) ?? "hardware encoder";
                return transform;
            }
            catch (Exception)
            {
                // Try the next one (e.g. a stale driver registration).
            }
        }
        return null;
    }

    /// <summary>
    /// Activates a decoder for the codec. Microsoft's decoders are registered as software MFTs
    /// but run on the GPU through DXVA once a D3D11 device manager is attached.
    /// </summary>
    public static IMFTransform? CreateDecoder(VideoCodec codec, out string name)
    {
        name = string.Empty;
        GpuDevice.EnsureMediaFoundation();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = Subtype(codec) };
        using var activates = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, FlagSync | FlagAsync | FlagHardware | FlagSortAndFilter, input, null);
        foreach (var activate in activates)
        {
            try
            {
                var transform = activate.ActivateObject<IMFTransform>();
                name = SafeTry.Run(() => activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)) ?? "decoder";
                return transform;
            }
            catch (Exception) { }
        }
        return null;
    }

    public static bool HasHardwareEncoder(VideoCodec codec)
    {
        lock (EncoderCache)
        {
            if (EncoderCache.TryGetValue(codec, out var cached))
                return cached && !IsBroken(codec);
            var transform = SafeTry.Run(() => CreateHardwareEncoder(codec, out _));
            var available = transform is not null;
            transform?.Dispose();
            EncoderCache[codec] = available;
            return available && !IsBroken(codec);
        }
    }

    public static bool HasDecoder(VideoCodec codec)
    {
        lock (DecoderCache)
        {
            if (DecoderCache.TryGetValue(codec, out var cached))
                return cached;
            var transform = SafeTry.Run(() => CreateDecoder(codec, out _));
            var available = transform is not null;
            transform?.Dispose();
            DecoderCache[codec] = available;
            return available;
        }
    }

    /// <summary>
    /// Resolves the settings preference to something this machine can actually encode. With a
    /// device, a hardware codec must also pass <see cref="Probe"/> (encode a few frames for real).
    /// </summary>
    public static VideoCodec Resolve(string preference, GpuDevice? probeOn = null)
    {
        bool Usable(VideoCodec codec) => HasHardwareEncoder(codec) && (probeOn is null || Probe(probeOn, codec));

        switch (EncoderPreference.Normalize(preference))
        {
            case EncoderPreference.Hevc:
                return Usable(VideoCodec.Hevc) ? VideoCodec.Hevc : Usable(VideoCodec.H264) ? VideoCodec.H264 : VideoCodec.Vp8;
            case EncoderPreference.Vp8:
                return VideoCodec.Vp8;
            default:
                return Usable(VideoCodec.H264) ? VideoCodec.H264 : VideoCodec.Vp8;
        }
    }

    /// <summary>A hardware encoder that built but never produced anything; not offered again this run.</summary>
    public static void MarkBroken(VideoCodec codec, string why)
    {
        lock (ProbeCache)
            ProbeCache[codec] = false;
        Diag.Log($"encoder: {codec} marked broken for this run ({why})");
    }

    private static bool IsBroken(VideoCodec codec)
    {
        lock (ProbeCache)
            return ProbeCache.TryGetValue(codec, out var works) && !works;
    }

    /// <summary>
    /// Encodes a handful of blank 1280x720 frames and waits for the first one out. Some drivers
    /// activate and accept the media types and then never produce a frame (Intel Quick Sync on an
    /// i5-1235U, 2.9.7 field report); without this the broadcast is live and black. Runs once per
    /// codec per run, on its own thread, so a driver that hangs costs a few seconds and nothing more.
    /// </summary>
    public static bool Probe(GpuDevice gpu, VideoCodec codec)
    {
        lock (ProbeCache)
        {
            if (ProbeCache.TryGetValue(codec, out var known))
                return known;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var works = false;
        var detail = "timed out (driver hung)";
        var worker = new Thread(() =>
        {
            MfVideoEncoder? encoder = null;
            Vortice.Direct3D11.ID3D11Texture2D? nv12 = null;
            try
            {
                using var done = new ManualResetEventSlim();
                nv12 = gpu.CreateTexture(Vortice.DXGI.Format.NV12, 1280, 720, Vortice.Direct3D11.BindFlags.RenderTarget);
                encoder = new MfVideoEncoder(gpu, codec, 1280, 720, 30, 2500);
                encoder.FrameEncoded += (_, _) => SafeTry.Run(done.Set);
                var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 3;
                var timestamp = 0L;
                while (!done.IsSet && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                {
                    if (encoder.WantsInput)
                    {
                        lock (gpu.ContextLock)
                            encoder.TrySubmit(nv12, timestamp);
                        timestamp += 33;
                    }
                    done.Wait(15);
                }
                works = done.IsSet;
                detail = encoder.Describe();
            }
            catch (Exception ex)
            {
                detail = "failed: " + ex.Message;
            }
            finally
            {
                SafeTry.Run(() => encoder?.Dispose());
                SafeTry.Run(() => nv12?.Dispose());
            }
        })
        { Name = "Beamcast encoder probe", IsBackground = true };
        worker.Start();
        var finished = worker.Join(TimeSpan.FromSeconds(6));
        if (!finished)
            works = false;

        Diag.Log($"encoder: probe {codec} {(works ? "ok" : "FAILED")} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms: {(finished ? detail : "timed out (driver hung)")}");
        lock (ProbeCache)
            ProbeCache[codec] = works;
        return works;
    }
}
