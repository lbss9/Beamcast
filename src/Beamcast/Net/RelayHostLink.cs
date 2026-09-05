using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Beamcast.Net;

/// <summary>
/// The host's side of a relay room: one WebSocket that carries every viewer, multiplexed.
/// Outbound frames go through a single queue so the socket is written from one place; the number
/// of queued video frames is exposed so the host can gate its own upload the way it gates viewers.
/// </summary>
public sealed class RelayHostLink : IDisposable
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket;
    private readonly Channel<(byte[] Frame, bool IsVideo)> _outbox =
        Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly byte[] _scratch = new byte[64 * 1024];
    private CancellationTokenSource? _cts;
    private int _pendingVideo;
    private int _closed;

    private RelayHostLink(ClientWebSocket socket, string room)
    {
        _socket = socket;
        Room = room;
    }

    public string Room { get; }

    public int PendingBroadcastFrames => Volatile.Read(ref _pendingVideo);

    public event Action<uint>? ViewerJoined;
    public event Action<uint>? ViewerLeft;
    public event Action<uint, Message>? DataReceived;
    public event Action<string>? Closed;

    public static async Task<RelayHostLink> ConnectAsync(string relayUrl, string? appKey, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(JoinTimeout);
            await socket.ConnectAsync(new Uri(relayUrl), timeout.Token).ConfigureAwait(false);

            var join = new RelayJoin { Role = RelayProtocol.RoleHost, AppKey = appKey };
            await socket.SendAsync(Json.Serialize(join), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

            var result = await RelayClient.ReadJoinResultAsync(socket, timeout.Token).ConfigureAwait(false);
            if (result is null || !result.Ok || !RelayProtocol.IsValidRoomCode(result.Room))
                throw new RelayException(result?.Reason ?? RelayProtocol.ReasonBadRequest);

            return new RelayHostLink(socket, result.Room!);
        }
        catch (RelayException)
        {
            socket.Dispose();
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new RelayException("timeout");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException)
        {
            socket.Dispose();
            throw new RelayException("unreachable", ex);
        }
    }

    public void Start(CancellationToken ct)
    {
        Diag.Log($"relay-link: start room {Room} state {_socket.State}");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = SendLoopAsync(_cts.Token);
        _ = ReceiveLoopAsync(_cts.Token);
    }

    /// <summary>Sends a framed message to every joined viewer (the relay fans it out).</summary>
    public void SendBroadcast(byte[] framed, bool isVideo)
    {
        if (isVideo)
            Interlocked.Increment(ref _pendingVideo);
        _outbox.Writer.TryWrite((RelayMux.Encode(RelayMux.Broadcast, RelayMux.KindData, framed), isVideo));
    }

    /// <summary>Sends a framed message to one viewer.</summary>
    public void SendTo(uint viewerId, byte[] framed) =>
        _outbox.Writer.TryWrite((RelayMux.Encode(viewerId, RelayMux.KindData, framed), false));

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            var reader = _outbox.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    await _socket.SendAsync(item.Frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    if (item.IsVideo)
                        Interlocked.Decrement(ref _pendingVideo);
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log("relay-link: send loop ended: " + ex.Message);
        }
        finally
        {
            Close("lost");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reason = "closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await WebSocketTransport.ReadFrameAsync(_socket, _scratch, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    Diag.Log($"relay-link: receive got null frame, state {_socket.State} close {_socket.CloseStatus}");
                    break;
                }
                if (!RelayMux.TryDecode(frame, out var viewerId, out var kind, out var framed))
                    continue;
                Diag.Log($"relay-link: frame kind {kind} viewer {viewerId} {framed.Length} B");

                switch (kind)
                {
                    case RelayMux.KindJoined:
                        ViewerJoined?.Invoke(viewerId);
                        break;
                    case RelayMux.KindLeft:
                        ViewerLeft?.Invoke(viewerId);
                        break;
                    case RelayMux.KindData:
                        if (Framing.TryDecodeWhole(framed, out var message))
                            DataReceived?.Invoke(viewerId, message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "left";
        }
        catch (Exception ex)
        {
            Diag.Log("relay-link: receive loop ended: " + ex.Message);
            reason = "lost";
        }
        Diag.Log("relay-link: closed " + reason);
        Close(reason);
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _outbox.Writer.TryComplete();
        SafeTry.Run(() => _cts?.Cancel());
        SafeTry.Run(() => _socket.Abort());
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        Close("left");
        SafeTry.Run(() => _socket.Dispose());
        _cts?.Dispose();
    }
}

/// <summary>A viewer as seen by the host through the relay: an inbound queue fed by the mux.</summary>
public sealed class RelayViewerTransport : IMessageTransport
{
    private readonly RelayHostLink _link;
    private readonly uint _viewerId;
    private readonly Channel<Message> _inbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });

    public RelayViewerTransport(RelayHostLink link, uint viewerId)
    {
        _link = link;
        _viewerId = viewerId;
    }

    public void Deliver(Message message) => _inbox.Writer.TryWrite(message);

    public Task WriteFramedAsync(byte[] framed, CancellationToken ct)
    {
        _link.SendTo(_viewerId, framed);
        return Task.CompletedTask;
    }

    public async Task<Message?> ReadAsync(CancellationToken ct)
    {
        try
        {
            return await _inbox.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Dispose() => _inbox.Writer.TryComplete();
}

public sealed class RelayException : Exception
{
    public RelayException(string reason, Exception? inner = null)
        : base("Relay: " + reason, inner)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

/// <summary>Bits shared by both ends of a relay conversation.</summary>
public static class RelayClient
{
    public static async Task<RelayJoinResult?> ReadJoinResultAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var assembled = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            assembled.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        return Json.Deserialize<RelayJoinResult>(Encoding.UTF8.GetString(assembled.ToArray()));
    }
}
