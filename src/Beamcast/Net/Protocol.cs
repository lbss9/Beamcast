using System.Buffers.Binary;

namespace Beamcast.Net;

/// <summary>Message kinds carried inside lounge frames. Values are part of the protocol; never renumber.</summary>
public enum MessageType : byte
{
    Video = 5,
    Audio = 12,
    Presence = 20,
    StreamMeta = 21,
}

/// <summary>Bits of the per-message flags byte. The server reads these without decrypting anything.</summary>
public static class MessageFlags
{
    public const byte None = 0;

    /// <summary>Video message carries a keyframe (lets the server resync a lagging subscriber).</summary>
    public const byte Keyframe = 0x01;

    /// <summary>Body is AES-GCM ciphertext: [12-byte nonce][ciphertext][16-byte tag].</summary>
    public const byte Encrypted = 0x80;
}

/// <summary>A framed message as read from a transport.</summary>
public readonly record struct Message(MessageType Type, byte Flags, byte[] Payload)
{
    public bool IsEncrypted => (Flags & MessageFlags.Encrypted) != 0;
    public bool IsKeyframe => (Flags & MessageFlags.Keyframe) != 0;
}

/// <summary>
/// Length-prefixed framing: <c>[int32 LE length][byte type][byte flags][body]</c>, where length
/// counts the type and flags bytes plus the body. Pure logic so it can be unit tested without sockets.
/// </summary>
public static class Framing
{
    public const int HeaderSize = 4;
    public const int PrefixSize = 2;

    /// <summary>Hard cap on a single message; protects both sides against a hostile length prefix.</summary>
    public const int MaxMessageSize = 32 * 1024 * 1024;

    public static byte[] Encode(MessageType type, ReadOnlySpan<byte> body, byte flags = MessageFlags.None)
    {
        if (body.Length + PrefixSize > MaxMessageSize)
            throw new ArgumentException("Message too large.", nameof(body));

        var buffer = new byte[HeaderSize + PrefixSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length + PrefixSize);
        buffer[HeaderSize] = (byte)type;
        buffer[HeaderSize + 1] = flags;
        body.CopyTo(buffer.AsSpan(HeaderSize + PrefixSize));
        return buffer;
    }

    /// <summary>Reads the length prefix; returns false when it is malformed.</summary>
    public static bool TryReadLength(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < HeaderSize)
            return false;
        var value = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (value < PrefixSize || value > MaxMessageSize)
            return false;
        length = value;
        return true;
    }

    /// <summary>
    /// Tries to pull one complete message out of <paramref name="buffer"/>. On success the number of
    /// consumed bytes is returned so the caller can advance its cursor.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out Message message, out int consumed)
    {
        message = default;
        consumed = 0;
        if (!TryReadLength(buffer, out var length))
            return false;
        if (buffer.Length < HeaderSize + length)
            return false;

        message = new Message(
            (MessageType)buffer[HeaderSize],
            buffer[HeaderSize + 1],
            buffer.Slice(HeaderSize + PrefixSize, length - PrefixSize).ToArray()
        );
        consumed = HeaderSize + length;
        return true;
    }

    /// <summary>Parses a message that has already been cut out of the stream (e.g. one WebSocket frame).</summary>
    public static bool TryDecodeWhole(ReadOnlySpan<byte> framed, out Message message) =>
        TryDecode(framed, out message, out var consumed) && consumed == framed.Length;

    /// <summary>Peeks the type and flags of a framed message without copying it.</summary>
    public static bool TryPeek(ReadOnlySpan<byte> framed, out MessageType type, out byte flags)
    {
        type = default;
        flags = 0;
        if (framed.Length < HeaderSize + PrefixSize)
            return false;
        type = (MessageType)framed[HeaderSize];
        flags = framed[HeaderSize + 1];
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

/// <summary>Binary header that precedes every encoded audio frame (Opus, 48 kHz).</summary>
public readonly record struct AudioPacketHeader(uint Sequence, long TimestampMs, int SampleRate, byte Channels)
{
    public const int Size = 4 + 8 + 4 + 1;

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], TimestampMs);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], SampleRate);
        destination[16] = Channels;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out AudioPacketHeader header)
    {
        header = default;
        if (source.Length < Size)
            return false;
        header = new AudioPacketHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(source),
            BinaryPrimitives.ReadInt64LittleEndian(source[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[12..]),
            source[16]
        );
        return header.SampleRate > 0 && header.Channels is 1 or 2;
    }
}

public static class AudioPacket
{
    public static byte[] Build(AudioPacketHeader header, ReadOnlySpan<byte> opus)
    {
        var body = new byte[AudioPacketHeader.Size + opus.Length];
        header.Write(body);
        opus.CopyTo(body.AsSpan(AudioPacketHeader.Size));
        return body;
    }

    public static bool TryParse(ReadOnlyMemory<byte> body, out AudioPacketHeader header, out ReadOnlyMemory<byte> opus)
    {
        opus = default;
        if (!AudioPacketHeader.TryRead(body.Span, out header))
            return false;
        opus = body[AudioPacketHeader.Size..];
        return opus.Length > 0;
    }
}
