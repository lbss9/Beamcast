using System.Net.WebSockets;

namespace Beamcast.Net;

/// <summary>A duplex channel of framed messages: TCP stream or WebSocket, direct or via the relay.</summary>
public interface IMessageTransport : IDisposable
{
    /// <summary>Writes one already-framed message.</summary>
    Task WriteFramedAsync(byte[] framed, CancellationToken ct);

    /// <summary>Reads one message; null on a clean end of stream.</summary>
    Task<Message?> ReadAsync(CancellationToken ct);
}

/// <summary>Reads and writes framed messages over a <see cref="Stream"/>.</summary>
public static class MessageStream
{
    public static async Task WriteAsync(Stream stream, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct, byte flags = MessageFlags.None)
    {
        var framed = Framing.Encode(type, payload.Span, flags);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync<T>(Stream stream, MessageType type, T value, CancellationToken ct) =>
        WriteAsync(stream, type, Json.Serialize(value), ct);

    /// <summary>Reads one message. Returns null on a clean end of stream.</summary>
    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[Framing.HeaderSize];
        if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false))
            return null;
        if (!Framing.TryReadLength(header, out var length))
            throw new InvalidDataException("Malformed message length.");

        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false))
            return null;

        var payload = new byte[length - Framing.PrefixSize];
        Buffer.BlockCopy(body, Framing.PrefixSize, payload, 0, payload.Length);
        return new Message((MessageType)body[0], body[1], payload);
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

/// <summary>Framed messages over a TCP <see cref="Stream"/>.</summary>
public sealed class StreamTransport : IMessageTransport
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public StreamTransport(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    public async Task WriteFramedAsync(byte[] framed, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(framed, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<Message?> ReadAsync(CancellationToken ct) => MessageStream.ReadAsync(_stream, ct);

    public void Dispose()
    {
        SafeTry.Run(() => _stream.Dispose());
        SafeTry.Run(() => _owner?.Dispose());
        _writeLock.Dispose();
    }
}

/// <summary>Framed messages over a WebSocket, one message per binary frame.</summary>
public sealed class WebSocketTransport : IMessageTransport
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _receiveBuffer = new byte[64 * 1024];

    public WebSocketTransport(WebSocket socket)
    {
        _socket = socket;
    }

    public WebSocket Socket => _socket;

    public async Task WriteFramedAsync(byte[] framed, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(framed, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<Message?> ReadAsync(CancellationToken ct)
    {
        var frame = await ReadFrameAsync(_socket, _receiveBuffer, ct).ConfigureAwait(false);
        if (frame is null)
            return null;
        if (!Framing.TryDecodeWhole(frame, out var message))
            throw new InvalidDataException("Malformed WebSocket message.");
        return message;
    }

    /// <summary>Reassembles one complete binary WebSocket message; null when the peer closed.</summary>
    public static async Task<byte[]?> ReadFrameAsync(WebSocket socket, byte[] scratch, CancellationToken ct)
    {
        using var assembled = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(scratch, ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            assembled.Write(scratch, 0, result.Count);
            if (assembled.Length > Framing.MaxMessageSize + RelayMux.HeaderSize)
                throw new InvalidDataException("WebSocket message too large.");
            if (result.EndOfMessage)
                return assembled.ToArray();
        }
    }

    public void Dispose()
    {
        SafeTry.Run(() => _socket.Abort());
        SafeTry.Run(() => _socket.Dispose());
        _writeLock.Dispose();
    }
}
