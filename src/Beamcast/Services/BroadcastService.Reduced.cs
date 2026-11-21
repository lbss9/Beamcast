using System.Diagnostics;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Beamcast.Services;

/// <summary>
/// The reduced stream: a second hardware encoder, at a smaller size and a quarter of the bitrate,
/// that runs only while some viewer on a direct link cannot keep up with the full stream. Each
/// such viewer is moved to it (and later tried on the full one again) by <see cref="ViewerQuality"/>;
/// everybody else keeps the full picture. Viewers on the host's relay always get the full stream,
/// because the host forwards one version of each stream.
/// </summary>
public sealed partial class BroadcastService
{
    private const int ReducedRingSize = 4;

    private readonly Dictionary<uint, ViewerQuality> _quality = new();
    private MfVideoEncoder? _reducedEncoder;
    private VideoProcessorConverter? _reducedConverter;
    private readonly ID3D11Texture2D?[] _reducedRing = new ID3D11Texture2D?[ReducedRingSize];
    private (int Width, int Height) _reducedRingSize;
    private int _reducedRingIndex;
    private volatile bool _reducedWanted;
    private volatile bool _reducedBroken;
    private int _reducedKeyframeRequested;
    private int _reducedKbps;
    private long _reducedRebuildTicks;
    private uint _reducedTick;

    /// <summary>The reduced stream exists for hardware codecs, while adaptive quality is on and its encoder works.</summary>
    private bool CanReduce => _adaptiveEnabled && ActiveCodec.IsGpu() && !_reducedBroken;

    private void OnReducedKeyframeRequested(uint streamId)
    {
        if (streamId != _streamId)
            return;
        Interlocked.Exchange(ref _reducedKeyframeRequested, 1);
    }

    /// <summary>
    /// A direct viewer is struggling: move it to the reduced stream after two complaints. True when
    /// that viewer is being taken care of this way, so it does not count against everyone's bitrate.
    /// </summary>
    private bool TryReduceViewer(uint viewerId, string why)
    {
        var streamId = _streamId;
        if (!CanReduce || !_lounge.HasDirectLink(streamId, viewerId))
            return false;
        bool reduceNow;
        lock (_quality)
        {
            if (!_quality.TryGetValue(viewerId, out var quality))
                _quality[viewerId] = quality = new ViewerQuality();
            if (quality.Reduced)
            {
                quality.Complain(Environment.TickCount64); // struggling even on the reduced stream
                return false;
            }
            reduceNow = quality.Complain(Environment.TickCount64);
        }
        if (!reduceNow)
            return true;
        if (!_lounge.SetViewerReduced(streamId, viewerId, reduced: true))
        {
            lock (_quality)
                _quality.Remove(viewerId);
            return false;
        }
        _reducedWanted = true;
        Diag.Log($"broadcast: viewer {viewerId} ({why}) moved to the reduced stream; the others keep the full one");
        return true;
    }

    /// <summary>Once per stats window: viewers whose quiet spell is over go back to the full stream.</summary>
    private void QualityTick()
    {
        var streamId = _streamId;
        List<uint> restore = [];
        lock (_quality)
        {
            if (_quality.Count == 0)
            {
                _reducedWanted = false;
                return;
            }
            var now = Environment.TickCount64;
            foreach (var (viewerId, quality) in _quality.ToList())
            {
                if (!_lounge.HasDirectLink(streamId, viewerId))
                {
                    _quality.Remove(viewerId); // back on the relay, which carries the full stream
                    continue;
                }
                if (!quality.Reduced)
                    continue;
                if (!CanReduce)
                {
                    quality.ForceFull();
                    restore.Add(viewerId);
                }
                else if (quality.Tick(now))
                {
                    restore.Add(viewerId);
                }
            }
        }
        foreach (var viewerId in restore)
        {
            Diag.Log($"broadcast: viewer {viewerId} back on the full stream");
            _lounge.SetViewerReduced(streamId, viewerId, reduced: false);
        }
        _reducedWanted = _lounge.AnyReducedViewer(streamId);
    }

    private void ForgetViewerQuality(uint viewerId)
    {
        lock (_quality)
            _quality.Remove(viewerId);
    }

    private void ResetReduced()
    {
        lock (_quality)
            _quality.Clear();
        _reducedWanted = false;
        _reducedBroken = false;
        Interlocked.Exchange(ref _reducedKeyframeRequested, 0);
    }

    /// <summary>Capture thread, context lock held: feeds the reduced encoder while somebody needs it.</summary>
    private void EncodeReduced(GpuFrame frame, int fullWidth, int fullHeight)
    {
        if (!_reducedWanted || _reducedBroken)
        {
            if (_reducedEncoder is not null)
            {
                Diag.Log("broadcast: nobody on the reduced stream, its encoder is released");
                DisposeReduced();
            }
            return;
        }

        var (width, height) = QualityPreset.ReducedSize(fullWidth, fullHeight);
        var fps = QualityPreset.ReducedFps(_fps);
        if (width <= 0 || height <= 0)
            return;
        // Half the frame rate: every other captured frame.
        if (fps != _fps && (++_reducedTick & 1) == 1)
            return;

        var kbps = QualityPreset.ReducedBitrate(EffectiveBitrateKbps);
        try
        {
            var encoder = _reducedEncoder;
            if (encoder is not null && encoder.KeyframeOverdue && Environment.TickCount64 - _reducedRebuildTicks >= RebuildGapMs)
            {
                _reducedRebuildTicks = Environment.TickCount64;
                encoder.Dispose();
                encoder = _reducedEncoder = null;
            }
            if (encoder is null || encoder.Width != width || encoder.Height != height || encoder.Fps != fps || encoder.Codec != ActiveCodec)
            {
                encoder?.Dispose();
                _reducedEncoder = null;
                encoder = new MfVideoEncoder(Gpu, ActiveCodec, width, height, fps, kbps, MfCodecs.Tuning);
                encoder.FrameEncoded += OnReducedFrameEncoded;
                encoder.Faulted += ex => MarkReducedBroken("encoder faulted: " + ex.Message);
                _reducedEncoder = encoder;
                _reducedKbps = kbps;
                Interlocked.Exchange(ref _reducedKeyframeRequested, 0); // it opens with a keyframe
                Diag.Log($"broadcast: reduced encoder {encoder.Name} {width}x{height}@{fps} {kbps} kbps");
            }
            else if (_reducedKbps != kbps)
            {
                encoder.SetBitrate(kbps);
                _reducedKbps = kbps;
            }

            if (!encoder.WantsInput)
                return;

            _reducedConverter ??= new VideoProcessorConverter(Gpu);
            if (_reducedRingSize != (width, height))
            {
                DisposeReducedRing();
                _reducedRingSize = (width, height);
            }
            _reducedRingIndex = (_reducedRingIndex + 1) % _reducedRing.Length;
            var nv12 = _reducedRing[_reducedRingIndex] ??= Gpu.CreateTexture(Format.NV12, width, height, BindFlags.RenderTarget);
            var box = QualityPreset.Letterbox(frame.Width, frame.Height, width, height);
            RectI? destRect = box is { } b ? new RectI(b.X, b.Y, b.Width, b.Height) : null;
            _reducedConverter.Convert(frame.Texture, 0, frame.Width, frame.Height, false, nv12, width, height, true, destRect);

            if (Interlocked.Exchange(ref _reducedKeyframeRequested, 0) == 1)
                encoder.RequestKeyframe();
            encoder.TrySubmit(nv12, frame.TimestampMs);
        }
        catch (Exception ex)
        {
            // A second encoder session is not a given on every driver. The broadcast itself must
            // not suffer for it: give the reduced stream up and put its viewers back on the full one.
            MarkReducedBroken(ex.Message);
        }
    }

    private void MarkReducedBroken(string why)
    {
        if (_reducedBroken)
            return;
        _reducedBroken = true;
        Diag.Log("broadcast: reduced stream unavailable for this broadcast (" + why + "); its viewers go back to the full one");
    }

    private void OnReducedFrameEncoded(EncodedFrame frame, double encodeMs)
    {
        var streamId = _streamId;
        if (!_live || streamId == 0)
            return;
        if (frame.IsKeyframe)
            Diag.Log($"broadcast: reduced frame #{frame.Sequence} key {frame.Width}x{frame.Height} {frame.Data.Length} B");
        Span<byte> header = stackalloc byte[VideoPacketHeader.Size];
        new VideoPacketHeader(frame.Sequence, frame.TimestampMs, frame.Width, frame.Height, frame.IsKeyframe).Write(header);
        _lounge.SendMedia(streamId, MessageType.Video, header, frame.Data, frame.IsKeyframe, _lounge.ServerClockNow(), relayGate: null, variant: 1);
    }

    private void DisposeReduced()
    {
        var encoder = _reducedEncoder;
        _reducedEncoder = null;
        SafeTry.Run(() => encoder?.Dispose());
        SafeTry.Run(() => _reducedConverter?.Dispose());
        _reducedConverter = null;
        DisposeReducedRing();
        _reducedRingSize = default;
    }

    private void DisposeReducedRing()
    {
        for (var i = 0; i < _reducedRing.Length; i++)
        {
            _reducedRing[i]?.Dispose();
            _reducedRing[i] = null;
        }
    }
}
