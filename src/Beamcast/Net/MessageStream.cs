using System.Buffers;

namespace Beamcast.Net;

/// <summary>Reads and writes framed messages over a <see cref="Stream"/>.</summary>
public static class MessageStream
{
    public static async Task WriteAsync(
        Stream stream,
        MessageType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct
    )
    {
        var framed = Framing.Encode(type, payload.Span);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync<T>(Stream stream, MessageType type, T value, CancellationToken ct) =>
        WriteAsync(stream, type, Json.Serialize(value), ct);

    /// <summary>Reads one message. Returns null on a clean end of stream.</summary>
    public static async Task<(MessageType Type, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[Framing.HeaderSize];
        if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false))
            return null;
        if (!Framing.TryReadLength(header, out var length))
            throw new InvalidDataException("Malformed message length.");

        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false))
            return null;

        var type = (MessageType)body[0];
        var payload = new byte[length - 1];
        Buffer.BlockCopy(body, 1, payload, 0, payload.Length);
        return (type, payload);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);
            if (read <= 0)
                return false;
            offset += read;
        }
        return true;
    }
}
