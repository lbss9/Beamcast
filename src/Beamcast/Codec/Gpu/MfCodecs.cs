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
                return cached;
            var transform = SafeTry.Run(() => CreateHardwareEncoder(codec, out _));
            var available = transform is not null;
            transform?.Dispose();
            EncoderCache[codec] = available;
            return available;
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

    /// <summary>Resolves the settings preference to something this machine can actually encode.</summary>
    public static VideoCodec Resolve(string preference)
    {
        switch (EncoderPreference.Normalize(preference))
        {
            case EncoderPreference.H264:
                return HasHardwareEncoder(VideoCodec.H264) ? VideoCodec.H264 : VideoCodec.Vp8;
            case EncoderPreference.Hevc:
                return HasHardwareEncoder(VideoCodec.Hevc) ? VideoCodec.Hevc : HasHardwareEncoder(VideoCodec.H264) ? VideoCodec.H264 : VideoCodec.Vp8;
            case EncoderPreference.Vp8:
                return VideoCodec.Vp8;
            default:
                return HasHardwareEncoder(VideoCodec.H264) ? VideoCodec.H264 : VideoCodec.Vp8;
        }
    }
}
