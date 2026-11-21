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

/// <summary>From a captured texture to frames on the wire: preview, GPU and VP8 encoding, the uplink gate, audio.</summary>
public sealed partial class BroadcastService
{
    private void MaybePreview(GpuFrame frame)
    {
        var presenter = _preview;
        if (presenter is null || !presenter.IsAttached)
            return;
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastPreviewTicks, now) < PreviewInterval)
            return;
        var first = _lastPreviewTicks == 0;
        _lastPreviewTicks = now;
        try
        {
            presenter.Present(frame.Texture, 0, frame.Width, frame.Height, false);
        }
        catch (Exception ex)
        {
            // The preview is only for the person broadcasting; a hiccup there must never end the
            // capture and the stream with it. The next frame tries again.
            Diag.Log("broadcast: preview present failed: " + ex.Message);
            return;
        }
        if (first)
            Post(() => PreviewStarted?.Invoke());
    }

    private void EncodeOnGpu(GpuFrame frame)
    {
        var (width, height) = StreamSize(frame.Width, frame.Height);
        if (width <= 0 || height <= 0)
            return;

        EncodeReduced(frame, width, height);

        var encoder = _gpuEncoder;
        if (encoder is not null && encoder.KeyframeOverdue)
        {
            // The driver ignored the keyframe request; a fresh encoder starts with an IDR frame.
            // Rebuilding costs a full-size IDR, which on a slow uplink causes the next drop and the
            // next request: without this gap an encoder that never honours the property is rebuilt
            // twice a second for ever (seen on AMD at 2560x1080).
            var now = Environment.TickCount64;
            if (now - _lastRebuildTicks >= RebuildGapMs)
            {
                _lastRebuildTicks = now;
                Diag.Log("broadcast: keyframe overdue, recreating encoder");
                encoder.Dispose();
                encoder = null;
                _gpuEncoder = null;
            }
            else if (!_rebuildHeld)
            {
                _rebuildHeld = true;
                Diag.Log($"broadcast: keyframe overdue again less than {RebuildGapMs} ms after the last rebuild, carrying on with this encoder");
            }
        }
        if (encoder is null || encoder.Width != width || encoder.Height != height || encoder.Fps != _fps)
        {
            encoder?.Dispose();
            encoder = new MfVideoEncoder(Gpu, ActiveCodec, width, height, _fps, EffectiveBitrateKbps, MfCodecs.Tuning);
            // It opens with an IDR frame, so anything waiting for a keyframe is already served.
            Interlocked.Exchange(ref _keyframeRequested, 0);
            _rebuildHeld = false;
            encoder.FrameEncoded += OnGpuFrameEncoded;
            encoder.Faulted += ex => Post(() =>
            {
                Error?.Invoke(ex.Message);
                StopLive();
            });
            _gpuEncoder = encoder;
            EncoderName = encoder.Name;
            Diag.Log($"broadcast: encoder {encoder.Name} {width}x{height}@{_fps} {_bitrateKbps} kbps, {(encoder.IsVbr ? "variable" : "constant")} bitrate, screen content {MfCodecs.Tuning.ScreenContent}");
            Interlocked.Exchange(ref _keyframeRequested, 1);
        }

        // The encoder only takes a frame when it asked for one; otherwise this frame is dropped and
        // the next capture wins. That keeps the queue depth at zero, which is the whole point.
        if (!encoder.WantsInput)
            return;

        _converter ??= new VideoProcessorConverter(Gpu);
        var nv12 = NextNv12(width, height);
        var box = QualityPreset.Letterbox(frame.Width, frame.Height, width, height);
        RectI? destRect = box is { } b ? new RectI(b.X, b.Y, b.Width, b.Height) : null;
        _converter.Convert(frame.Texture, 0, frame.Width, frame.Height, false, nv12, width, height, true, destRect);

        if (Interlocked.Exchange(ref _keyframeRequested, 0) == 1)
            encoder.RequestKeyframe();
        encoder.TrySubmit(nv12, frame.TimestampMs);
    }

    private ID3D11Texture2D NextNv12(int width, int height)
    {
        if (_nv12Size != (width, height))
        {
            for (var i = 0; i < _nv12Ring.Length; i++)
            {
                _nv12Ring[i]?.Dispose();
                _nv12Ring[i] = null;
            }
            _nv12Size = (width, height);
        }

        _nv12Index = (_nv12Index + 1) % _nv12Ring.Length;
        return _nv12Ring[_nv12Index] ??= Gpu.CreateTexture(Format.NV12, width, height, BindFlags.RenderTarget);
    }

    private void OnGpuFrameEncoded(EncodedFrame frame, double encodeMs)
    {
        if (!_live)
            return;
        Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);
        if (frame.IsKeyframe || frame.Sequence % 120 == 1)
            Diag.Log($"broadcast: frame #{frame.Sequence} key={frame.IsKeyframe} {frame.Data.Length} B");
        Publish(frame);
        AccountFrame(frame, encodeMs);
    }

    /// <summary>
    /// Sends a frame once to the host (which fans it out) and to each direct link. A slow uplink
    /// to the host drops frames here, not in the server; each direct link has its own gate.
    /// </summary>
    private void Publish(EncodedFrame frame)
    {
        var streamId = _streamId;
        if (streamId == 0)
            return;

        // Header and bitstream go in as two parts and are joined once, inside the encrypted frame.
        Span<byte> header = stackalloc byte[VideoPacketHeader.Size];
        new VideoPacketHeader(frame.Sequence, frame.TimestampMs, frame.Width, frame.Height, frame.IsKeyframe).Write(header);
        _lounge.SendMedia(streamId, MessageType.Video, header, frame.Data, frame.IsKeyframe, _lounge.ServerClockNow(),
            relayGate: () => RelayAllows(streamId, frame.IsKeyframe));
    }

    private bool RelayAllows(uint streamId, bool keyframe)
    {
        GateDecision decision;
        lock (_upstreamGate)
        {
            decision = _upstreamGate.Offer(keyframe, _lounge.PendingVideo(streamId));
        }
        if (decision == GateDecision.DropAndRequestKeyframe)
        {
            AdaptOnDrop();
            Interlocked.Exchange(ref _keyframeRequested, 1);
            _gpuEncoder?.RequestKeyframe();
            return false;
        }
        if (decision != GateDecision.Send)
        {
            AdaptOnDrop();
            return false;
        }
        return true;
    }

    private void OnAudioPacket(byte[] packet)
    {
        if (!_live || _paused || _streamId == 0 || IsStandby)
            return;
        _lounge.SendMedia(_streamId, MessageType.Audio, packet, false);
        Interlocked.Add(ref _statsAudioBytes, packet.Length);
    }

    private void QueueForVp8(GpuFrame frame)
    {
        var raw = _capture!.ReadPixels(frame);
        var previous = Interlocked.Exchange(ref _latest, raw);
        if (previous is not null)
            ArrayPool<byte>.Shared.Return(previous.Pixels);
        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException) { }
    }

    private void Vp8Loop(CancellationToken ct)
    {
        using var encoder = new Vp8Encoder { BitrateKbps = EffectiveBitrateKbps };
        byte[]? encodeBuffer = null;
        var lastKeyframe = Stopwatch.GetTimestamp();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_frameSignal.Wait(250, ct))
                    continue;

                var raw = Interlocked.Exchange(ref _latest, null);
                if (raw is null)
                    continue;

                try
                {
                    var (width, height) = StreamSize(raw.Width, raw.Height);
                    if (width <= 0 || height <= 0)
                        continue;

                    var needed = width * height * 4;
                    if (encodeBuffer is null || encodeBuffer.Length != needed)
                        encodeBuffer = new byte[needed];

                    var box = QualityPreset.Letterbox(raw.Width, raw.Height, width, height);
                    if (box is { } b)
                    {
                        // Different aspect: scale into a centred box on black bars.
                        var inner = ArrayPool<byte>.Shared.Rent(b.Width * b.Height * 4);
                        try
                        {
                            FrameScaler.Resize(raw.Pixels, raw.Width, raw.Height, inner, b.Width, b.Height);
                            Array.Clear(encodeBuffer, 0, needed);
                            for (var row = 0; row < b.Height; row++)
                                Buffer.BlockCopy(inner, row * b.Width * 4, encodeBuffer, ((b.Y + row) * width + b.X) * 4, b.Width * 4);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(inner);
                        }
                    }
                    else if (width == raw.Width && height == raw.Height)
                        Buffer.BlockCopy(raw.Pixels, 0, encodeBuffer, 0, needed);
                    else
                        FrameScaler.Resize(raw.Pixels, raw.Width, raw.Height, encodeBuffer, width, height);

                    encoder.BitrateKbps = EffectiveBitrateKbps;
                    var wantKey = Interlocked.Exchange(ref _keyframeRequested, 0) == 1
                        || Stopwatch.GetElapsedTime(lastKeyframe) >= Vp8KeyframeInterval;
                    if (wantKey)
                        encoder.RequestKeyframe();

                    var start = Stopwatch.GetTimestamp();
                    var encoded = encoder.Encode(encodeBuffer, width, height, raw.TimestampMs);
                    var encodeMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (encoded is null)
                        continue;
                    if (encoded.IsKeyframe)
                        lastKeyframe = Stopwatch.GetTimestamp();

                    Publish(encoded);
                    AccountFrame(encoded, encodeMs);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(raw.Pixels);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Post(() =>
            {
                Error?.Invoke(ex.Message);
                StopLive();
            });
        }
    }
}
