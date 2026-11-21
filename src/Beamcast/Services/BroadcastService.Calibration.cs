using System.Buffers;
using System.Diagnostics;
using Beamcast.Audio;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Render;
using Microsoft.UI.Dispatching;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Beamcast.Services;

/// <summary>Profile calibration: short encode trials on the live capture to pick preset, codec and bitrate for this machine.</summary>
public sealed partial class BroadcastService
{
    // ----- profile calibration -----

    /// <summary>
    /// Measures this machine against the current source and applies the settings the profile
    /// asks for: every candidate size is encoded from live capture for a moment to get the encoder's
    /// time per frame and how many frames it kept up with; the upload to the host is timed when the
    /// host offers the probe. Runs in preview only (never while live). Returns null when nothing
    /// could be measured.
    /// </summary>
    public async Task<ProfileChoice?> CalibrateAsync(BroadcastProfile profile, IProgress<string>? progress, CancellationToken ct)
    {
        var source = Source ?? throw new InvalidOperationException("no-source");
        if (State != BroadcastState.Preview)
            throw new InvalidOperationException("live");
        var capture = _capture ?? throw new InvalidOperationException("no-source");

        var hasH264 = MfCodecs.HasHardwareEncoder(VideoCodec.H264);
        var hasHevc = MfCodecs.HasHardwareEncoder(VideoCodec.Hevc);
        var codec = hasH264 ? VideoCodec.H264 : hasHevc ? VideoCodec.Hevc : VideoCodec.Vp8;
        var samples = new List<EncodeSample>();
        Diag.Log($"profile: calibrating {profile} for {source.Width}x{source.Height}, encoders h264={hasH264} hevc={hasHevc}");
        capture.MaxFps = 60;
        try
        {
            foreach (var preset in BroadcastProfiles.Candidates(source.Width, source.Height))
            {
                ct.ThrowIfCancellationRequested();
                var (width, height) = QualityPreset.Fit(preset, source.Width, source.Height);
                progress?.Report(preset);
                if (codec == VideoCodec.Vp8)
                {
                    samples.Add(new EncodeSample(preset, width, height, 0, 0));
                    continue;
                }
                var run = new Calibration(Gpu, codec, preset);
                _calibration = run;
                try
                {
                    await Task.Delay(1400, ct);
                }
                finally
                {
                    _calibration = null;
                }
                await Task.Delay(60, ct); // let a frame in flight finish before tearing the encoder down
                var sample = run.Finish();
                samples.Add(sample);
                Diag.Log($"profile: {preset} {sample.Width}x{sample.Height}: {sample.EncodeMs:F1} ms/frame, {sample.EncodedFps:F0} fps kept");
            }

            var uplink = 0;
            var host = _lounge.ServerUrl;
            if (host.Length > 0)
            {
                progress?.Report("upload");
                uplink = await LoungeClient.MeasureUploadAsync(host, LoungeService.AppKeyFor(host), 1_500_000, TimeSpan.FromSeconds(8), ct);
                Diag.Log($"profile: upload to host {uplink} kbps");
            }

            var choice = BroadcastProfiles.Recommend(profile, new ProfileInputs(source.Width, source.Height, samples, uplink, hasH264, hasHevc));
            if (choice is null)
                return null;
            Preset = choice.Preset;
            Fps = choice.Fps;
            BitrateKbps = choice.BitrateKbps;
            EncoderPreferenceValue = choice.Encoder;
            Diag.Log($"profile: {profile} -> {choice.Preset} {choice.Width}x{choice.Height}@{choice.Fps} {choice.BitrateKbps} kbps {choice.Encoder}");
            return choice;
        }
        finally
        {
            _calibration = null;
            capture.MaxFps = _fps;
        }
    }

    /// <summary>One measured preset: a throwaway encoder fed from the live capture for about a second.</summary>
    private sealed class Calibration
    {
        private readonly GpuDevice _gpu;
        private readonly VideoCodec _codec;
        private readonly string _preset;
        private readonly VideoProcessorConverter _converter;
        private MfVideoEncoder? _encoder;
        private ID3D11Texture2D? _nv12;
        private int _width;
        private int _height;
        private int _encoded;
        private double _encodeMs;
        private long _firstTicks;
        private long _lastTicks;
        private volatile bool _closed;

        public Calibration(GpuDevice gpu, VideoCodec codec, string preset)
        {
            _gpu = gpu;
            _codec = codec;
            _preset = preset;
            _converter = new VideoProcessorConverter(gpu);
        }

        /// <summary>Capture thread, context lock held.</summary>
        public void Offer(GpuFrame frame)
        {
            if (_closed)
                return;
            try
            {
                if (_encoder is null)
                {
                    (_width, _height) = QualityPreset.Fit(_preset, frame.Width, frame.Height);
                    if (_width <= 0 || _height <= 0)
                        return;
                    _nv12 = _gpu.CreateTexture(Format.NV12, _width, _height, BindFlags.RenderTarget);
                    _encoder = new MfVideoEncoder(_gpu, _codec, _width, _height, 60, QualityPreset.SuggestedBitrate(_preset, 60, _codec.ToWireName()));
                    _encoder.FrameEncoded += (_, ms) =>
                    {
                        Interlocked.Increment(ref _encoded);
                        lock (this)
                            _encodeMs += ms;
                    };
                    _firstTicks = Stopwatch.GetTimestamp();
                }
                _lastTicks = Stopwatch.GetTimestamp();
                if (!_encoder.WantsInput)
                    return;
                var box = QualityPreset.Letterbox(frame.Width, frame.Height, _width, _height);
                RectI? dest = box is { } b ? new RectI(b.X, b.Y, b.Width, b.Height) : null;
                _converter.Convert(frame.Texture, 0, frame.Width, frame.Height, false, _nv12!, _width, _height, true, dest);
                _encoder.TrySubmit(_nv12!, frame.TimestampMs);
            }
            catch (Exception ex)
            {
                Diag.Log("profile: sample failed: " + ex.Message);
                _closed = true;
            }
        }

        public EncodeSample Finish()
        {
            _closed = true;
            var seconds = _firstTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_firstTicks, _lastTicks).TotalSeconds;
            var encoded = Volatile.Read(ref _encoded);
            double ms;
            lock (this)
                ms = encoded > 0 ? _encodeMs / encoded : 0;
            var fps = seconds > 0.2 ? encoded / seconds : 0;
            lock (_gpu.ContextLock)
            {
                _encoder?.Dispose();
                _encoder = null;
                if (_nv12 is not null)
                    _converter.Forget(_nv12);
                _nv12?.Dispose();
                _nv12 = null;
                _converter.Dispose();
            }
            return new EncodeSample(_preset, _width, _height, ms, fps);
        }
    }
}
