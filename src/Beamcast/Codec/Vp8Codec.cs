using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace Beamcast.Codec;

/// <summary>
/// VP8 encoder on top of libvpx. Not thread-safe: drive it from a single encoding thread.
/// A new libvpx instance is created whenever the frame size or bitrate changes, which
/// naturally produces a keyframe.
/// </summary>
public sealed class Vp8Encoder : IDisposable
{
    private VpxVideoEncoder? _encoder;
    private int _width;
    private int _height;
    private int _bitrateKbps;
    private bool _forceKeyframe;
    private uint _sequence;

    public int BitrateKbps
    {
        get => _bitrateKbps;
        set
        {
            var clamped = QualityPreset.ClampBitrate(value);
            if (clamped == _bitrateKbps)
                return;
            _bitrateKbps = clamped;
            Reset();
        }
    }

    public void RequestKeyframe() => _forceKeyframe = true;

    public EncodedFrame? Encode(byte[] bgra, int width, int height, long timestampMs)
    {
        if (_encoder is null || width != _width || height != _height)
        {
            Reset();
            _width = width;
            _height = height;
            _encoder = new VpxVideoEncoder { TargetKbps = (uint)Math.Max(QualityPreset.MinBitrateKbps, _bitrateKbps) };
            _forceKeyframe = true;
        }

        if (_forceKeyframe)
        {
            _encoder.ForceKeyFrame();
            _forceKeyframe = false;
        }

        var data = _encoder.EncodeVideo(width, height, bgra, VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8);
        if (data is null || data.Length == 0)
            return null;

        return new EncodedFrame(data, IsKeyframe(data), width, height, timestampMs, ++_sequence);
    }

    /// <summary>VP8 frame tag: bit 0 of the first byte is 0 for key frames.</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> vp8) => vp8.Length > 0 && (vp8[0] & 0x01) == 0;

    private void Reset()
    {
        _encoder?.Dispose();
        _encoder = null;
        _width = 0;
        _height = 0;
    }

    public void Dispose() => Reset();
}

/// <summary>Decoded output, always BGRA. The buffer comes from a small ring and is reused.</summary>
public sealed class DecodedFrame
{
    public DecodedFrame(byte[] bgra, int width, int height)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
    }

    public byte[] Bgra { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>VP8 decoder. libvpx hands back packed BGR; we expand to BGRA for the UI.</summary>
public sealed class Vp8Decoder : IDisposable
{
    private const int RingSize = 4;

    private readonly VpxVideoEncoder _decoder = new();
    private readonly byte[]?[] _ring = new byte[RingSize][];
    private int _ringIndex;

    public DecodedFrame? Decode(ReadOnlySpan<byte> vp8)
    {
        DecodedFrame? last = null;
        foreach (var sample in _decoder.DecodeVideo(vp8.ToArray(), VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.VP8))
        {
            var width = (int)sample.Width;
            var height = (int)sample.Height;
            if (width <= 0 || height <= 0 || sample.Sample is null)
                continue;
            last = Expand(sample.Sample, width, height);
        }
        return last;
    }

    private unsafe DecodedFrame? Expand(byte[] sample, int width, int height)
    {
        var pixels = width * height;
        var bytesPerPixel = sample.Length / pixels;
        if (bytesPerPixel is not (3 or 4))
            return null;

        var needed = pixels * 4;
        var slot = _ringIndex;
        _ringIndex = (_ringIndex + 1) % RingSize;
        var buffer = _ring[slot];
        if (buffer is null || buffer.Length < needed)
            _ring[slot] = buffer = new byte[needed];

        if (bytesPerPixel == 4)
        {
            Buffer.BlockCopy(sample, 0, buffer, 0, needed);
        }
        else
        {
            fixed (byte* src = sample)
            fixed (byte* dst = buffer)
            {
                var s = src;
                var d = (uint*)dst;
                for (var i = 0; i < pixels; i++)
                {
                    *d++ = 0xFF000000u | (uint)(s[2] << 16) | (uint)(s[1] << 8) | s[0];
                    s += 3;
                }
            }
        }

        return new DecodedFrame(buffer, width, height);
    }

    public void Dispose() => _decoder.Dispose();
}
