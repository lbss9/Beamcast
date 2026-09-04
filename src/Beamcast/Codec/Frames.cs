namespace Beamcast.Codec;

/// <summary>An uncompressed BGRA frame. <see cref="Pixels"/> may be longer than the image; use <see cref="ByteLength"/>.</summary>
public sealed class RawFrame
{
    public RawFrame(byte[] pixels, int width, int height, long timestampMs)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        TimestampMs = timestampMs;
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public long TimestampMs { get; }
    public int Stride => Width * 4;
    public int ByteLength => Width * Height * 4;
}

/// <summary>One compressed frame ready to be sent to viewers.</summary>
public sealed record EncodedFrame(
    byte[] Data,
    bool IsKeyframe,
    int Width,
    int Height,
    long TimestampMs,
    uint Sequence
);
