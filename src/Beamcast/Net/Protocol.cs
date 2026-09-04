using System.Buffers.Binary;

namespace Beamcast.Net;

/// <summary>Message kinds carried on the wire. Values are part of the protocol; never renumber.</summary>
public enum MessageType : byte
{
    Challenge = 1,
    Hello = 2,
    Welcome = 3,
    Reject = 4,
    Video = 5,
    Ping = 6,
    Pong = 7,
    Viewers = 8,
    KeyframeRequest = 9,
    StreamState = 10,
    Bye = 11,
}

/// <summary>
/// Length-prefixed framing: <c>[int32 LE length][byte type][payload]</c>, where length counts
/// the type byte plus the payload. Pure logic so it can be unit tested without sockets.
/// </summary>
public static class Framing
{
    public const int HeaderSize = 4;

    /// <summary>Hard cap on a single message; protects both sides against a hostile length prefix.</summary>
    public const int MaxMessageSize = 32 * 1024 * 1024;

    public static byte[] Encode(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length + 1 > MaxMessageSize)
            throw new ArgumentException("Message too large.", nameof(payload));

        var buffer = new byte[HeaderSize + 1 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, payload.Length + 1);
        buffer[HeaderSize] = (byte)type;
        payload.CopyTo(buffer.AsSpan(HeaderSize + 1));
        return buffer;
    }

    /// <summary>Reads the length prefix; returns false when it is malformed.</summary>
    public static bool TryReadLength(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < HeaderSize)
            return false;
        var value = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (value < 1 || value > MaxMessageSize)
            return false;
        length = value;
        return true;
    }

    /// <summary>
    /// Tries to pull one complete message out of <paramref name="buffer"/>. On success the number of
    /// consumed bytes is returned so the caller can advance its cursor.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> buffer,
        out MessageType type,
        out byte[] payload,
        out int consumed
    )
    {
        type = default;
        payload = [];
        consumed = 0;
        if (!TryReadLength(buffer, out var length))
            return false;
        if (buffer.Length < HeaderSize + length)
            return false;

        type = (MessageType)buffer[HeaderSize];
        payload = buffer.Slice(HeaderSize + 1, length - 1).ToArray();
        consumed = HeaderSize + length;
        return true;
    }
}

/// <summary>Binary header that precedes every encoded video frame.</summary>
public readonly record struct VideoPacketHeader(
    uint Sequence,
    long TimestampMs,
    int Width,
    int Height,
    bool IsKeyframe
)
{
    public const int Size = 4 + 8 + 4 + 4 + 1;

    private const byte KeyframeFlag = 0x01;

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], TimestampMs);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], Height);
        destination[20] = IsKeyframe ? KeyframeFlag : (byte)0;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out VideoPacketHeader header)
    {
        header = default;
        if (source.Length < Size)
            return false;
        header = new VideoPacketHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(source),
            BinaryPrimitives.ReadInt64LittleEndian(source[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[16..]),
            (source[20] & KeyframeFlag) != 0
        );
        return header.Width > 0 && header.Height > 0;
    }
}

/// <summary>Builds and parses the video message body: header followed by the codec bitstream.</summary>
public static class VideoPacket
{
    public static byte[] Build(VideoPacketHeader header, ReadOnlySpan<byte> bitstream)
    {
        var body = new byte[VideoPacketHeader.Size + bitstream.Length];
        header.Write(body);
        bitstream.CopyTo(body.AsSpan(VideoPacketHeader.Size));
        return body;
    }

    public static bool TryParse(
        ReadOnlyMemory<byte> body,
        out VideoPacketHeader header,
        out ReadOnlyMemory<byte> bitstream
    )
    {
        bitstream = default;
        if (!VideoPacketHeader.TryRead(body.Span, out header))
            return false;
        bitstream = body[VideoPacketHeader.Size..];
        return bitstream.Length > 0;
    }
}
