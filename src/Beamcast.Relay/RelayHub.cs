using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Beamcast.Net;

namespace Beamcast.Relay;

public sealed class RelayOptions
{
    /// <summary>When set, every client must present it. The relay never sees room secrets.</summary>
    public string? AppKey { get; init; }
}

/// <summary>
/// Rooms and the sockets in them. The relay is deliberately dumb: it forwards framed messages,
/// tags them with viewer ids for the host, and applies the per-viewer keyframe gate so a slow
/// viewer costs nothing to the others. It cannot read any message body.
/// </summary>
public sealed class RelayHub
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly RelayOptions _options;
    private readonly ILogger<RelayHub> _log;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    public RelayHub(RelayOptions options, ILogger<RelayHub> log)
    {
        _options = options;
        _log = log;
    }

    public object Snapshot() => new
    {
        rooms = _rooms.Count,
        viewers = _rooms.Values.Sum(r => r.ViewerCount),
        uptime = Environment.TickCount64 / 1000,
    };

    public async Task HandleAsync(WebSocket socket, string remote, CancellationToken ct)
    {
        RelayJoin? join;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(JoinTimeout);
            join = await ReadJoinAsync(socket, timeout.Token);
        }
        catch (Exception)
        {
            return;
        }

        if (join is null || join.Version != RelayProtocol.Version)
        {
            await ReplyAsync(socket, new RelayJoinResult { Ok = false, Reason = join is null ? RelayProtocol.ReasonBadRequest : RelayProtocol.ReasonVersion }, ct);
            return;
        }

        if (!KeyMatches(join.AppKey))
        {
            _log.LogWarning("Refused {Remote}: bad app key.", remote);
            await ReplyAsync(socket, new RelayJoinResult { Ok = false, Reason = RelayProtocol.ReasonBadKey }, ct);
            return;
        }

        if (join.Role == RelayProtocol.RoleHost)
            await RunHostAsync(socket, remote, ct);
        else
            await RunViewerAsync(socket, remote, join, ct);
    }

    private bool KeyMatches(string? presented)
    {
        if (string.IsNullOrEmpty(_options.AppKey))
            return true;
        var expected = Encoding.UTF8.GetBytes(_options.AppKey);
        var actual = Encoding.UTF8.GetBytes(presented ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private async Task RunHostAsync(WebSocket socket, string remote, CancellationToken ct)
    {
        Room room;
        while (true)
        {
            room = new Room(RelayProtocol.NewRoomCode(), socket, _log);
            if (_rooms.TryAdd(room.Code, room))
                break;
        }

        _log.LogInformation("Room {Room} opened by {Remote}.", room.Code, remote);
        await ReplyAsync(socket, new RelayJoinResult { Ok = true, Room = room.Code }, ct);

        try
        {
            await room.RunHostAsync(ct);
        }
        finally
        {
            _rooms.TryRemove(room.Code, out _);
            await room.CloseAsync();
            _log.LogInformation("Room {Room} closed ({Viewers} viewers peak).", room.Code, room.PeakViewers);
        }
    }

    private async Task RunViewerAsync(WebSocket socket, string remote, RelayJoin join, CancellationToken ct)
    {
        var code = RelayProtocol.NormalizeRoomCode(join.Room);
        if (!_rooms.TryGetValue(code, out var room))
        {
            await ReplyAsync(socket, new RelayJoinResult { Ok = false, Reason = RelayProtocol.ReasonNoRoom }, ct);
            return;
        }

        await ReplyAsync(socket, new RelayJoinResult { Ok = true, Room = code, Viewers = room.ViewerCount }, ct);
        _log.LogInformation("Viewer from {Remote} entered room {Room}.", remote, code);
        await room.RunViewerAsync(socket, ct);
    }

    private static async Task<RelayJoin?> ReadJoinAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var assembled = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            assembled.Write(buffer, 0, result.Count);
            if (assembled.Length > buffer.Length)
                return null;
            if (result.EndOfMessage)
                break;
        }
        return Json.Deserialize<RelayJoin>(Encoding.UTF8.GetString(assembled.ToArray()));
    }

    private static Task ReplyAsync(WebSocket socket, RelayJoinResult result, CancellationToken ct) =>
        socket.SendAsync(Json.Serialize(result), WebSocketMessageType.Text, true, ct);
}

/// <summary>One host and its viewers.</summary>
internal sealed class Room
{
    private const int MaxPendingFramesPerViewer = 4;
    private static readonly byte[] KeyframeRequestFrame = Framing.Encode(MessageType.KeyframeRequest, ReadOnlySpan<byte>.Empty);
    private static readonly byte[] ByeFrame = Framing.Encode(MessageType.Bye, ReadOnlySpan<byte>.Empty);

    private readonly WebSocket _host;
    private readonly ILogger _log;
    private readonly Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<uint, Viewer> _viewers = new();
    private readonly CancellationTokenSource _closed = new();
    private uint _nextViewerId;
    private int _peakViewers;

    public Room(string code, WebSocket host, ILogger log)
    {
        Code = code;
        _host = host;
        _log = log;
    }

    public string Code { get; }
    public int ViewerCount => _viewers.Count;
    public int PeakViewers => _peakViewers;

    public async Task RunHostAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var sender = SendToHostLoopAsync(linked.Token);
        var scratch = new byte[64 * 1024];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(_host, scratch, linked.Token);
                if (frame is null)
                    break;
                if (!RelayMux.TryDecode(frame, out var viewerId, out var kind, out var framed) || kind != RelayMux.KindData)
                    continue;

                var bytes = framed;
                if (viewerId == RelayMux.Broadcast)
                {
                    Framing.TryPeek(bytes, out var type, out var flags);
                    var isVideo = type == MessageType.Video;
                    var keyframe = (flags & MessageFlags.Keyframe) != 0;
                    foreach (var viewer in _viewers.Values)
                    {
                        if (!viewer.Joined)
                            continue;
                        if (isVideo)
                        {
                            if (viewer.OfferVideo(bytes, keyframe))
                                _toHost.Writer.TryWrite(RelayMux.Encode(viewer.Id, RelayMux.KindData, KeyframeRequestFrame));
                        }
                        else
                        {
                            viewer.Enqueue(bytes);
                        }
                    }
                }
                else if (_viewers.TryGetValue(viewerId, out var viewer))
                {
                    if (Framing.TryPeek(bytes, out var type, out _) && type == MessageType.Welcome)
                        viewer.Joined = true;
                    viewer.Enqueue(bytes);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Host loop ended.");
        }
        finally
        {
            _closed.Cancel();
            _toHost.Writer.TryComplete();
            await SafeAwait(sender);
        }
    }

    public async Task RunViewerAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextViewerId);
        var viewer = new Viewer(id, socket, MaxPendingFramesPerViewer);
        _viewers[id] = viewer;
        var count = _viewers.Count;
        if (count > _peakViewers)
            _peakViewers = count;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var sender = viewer.SendLoopAsync(linked.Token);
        _toHost.Writer.TryWrite(RelayMux.Encode(id, RelayMux.KindJoined, ReadOnlySpan<byte>.Empty));

        var scratch = new byte[64 * 1024];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var framed = await ReadFrameAsync(socket, scratch, linked.Token);
                if (framed is null)
                    break;
                if (framed.Length < Framing.HeaderSize + Framing.PrefixSize)
                    continue;
                _toHost.Writer.TryWrite(RelayMux.Encode(id, RelayMux.KindData, framed));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            _viewers.TryRemove(id, out _);
            _toHost.Writer.TryWrite(RelayMux.Encode(id, RelayMux.KindLeft, ReadOnlySpan<byte>.Empty));
            viewer.Complete();
            await SafeAwait(sender);
            if (_closed.IsCancellationRequested)
                await SafeSendAsync(socket, ByeFrame);
            await SafeCloseAsync(socket);
        }
    }

    public async Task CloseAsync()
    {
        _closed.Cancel();
        foreach (var viewer in _viewers.Values)
        {
            viewer.Complete();
            await SafeSendAsync(viewer.Socket, ByeFrame);
            await SafeCloseAsync(viewer.Socket);
        }
        _viewers.Clear();
    }

    private async Task SendToHostLoopAsync(CancellationToken ct)
    {
        try
        {
            var reader = _toHost.Reader;
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var frame))
                    await _host.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (Exception) { }
        finally
        {
            _closed.Cancel();
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(WebSocket socket, byte[] scratch, CancellationToken ct)
    {
        using var assembled = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(scratch, ct);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            assembled.Write(scratch, 0, result.Count);
            if (assembled.Length > Framing.MaxMessageSize + RelayMux.HeaderSize)
                return null;
            if (result.EndOfMessage)
                return assembled.ToArray();
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception) { }
    }

    private static async Task SafeSendAsync(WebSocket socket, byte[] frame)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(1000);
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, timeout.Token);
            }
        }
        catch (Exception) { }
    }

    private static async Task SafeCloseAsync(WebSocket socket)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(1000);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
        }
        catch (Exception) { }
    }

    /// <summary>A viewer socket with its own outbox and keyframe gate.</summary>
    private sealed class Viewer
    {
        private readonly Channel<(byte[] Bytes, bool IsVideo)> _outbox =
            Channel.CreateUnbounded<(byte[], bool)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly FrameGate _gate;
        private readonly object _gateLock = new();
        private int _pendingVideo;

        public Viewer(uint id, WebSocket socket, int maxPending)
        {
            Id = id;
            Socket = socket;
            _gate = new FrameGate(maxPending);
        }

        public uint Id { get; }
        public WebSocket Socket { get; }
        public volatile bool Joined;

        /// <summary>Returns true when the host should be asked for a keyframe on this viewer's behalf.</summary>
        public bool OfferVideo(byte[] framed, bool keyframe)
        {
            GateDecision decision;
            lock (_gateLock)
            {
                decision = _gate.Offer(keyframe, Volatile.Read(ref _pendingVideo));
            }
            switch (decision)
            {
                case GateDecision.Send:
                    Interlocked.Increment(ref _pendingVideo);
                    _outbox.Writer.TryWrite((framed, true));
                    return false;
                case GateDecision.DropAndRequestKeyframe:
                    return true;
                default:
                    return false;
            }
        }

        public void Enqueue(byte[] framed) => _outbox.Writer.TryWrite((framed, false));

        public void Complete() => _outbox.Writer.TryComplete();

        public async Task SendLoopAsync(CancellationToken ct)
        {
            try
            {
                var reader = _outbox.Reader;
                while (await reader.WaitToReadAsync(ct))
                {
                    while (reader.TryRead(out var item))
                    {
                        await Socket.SendAsync(item.Bytes, WebSocketMessageType.Binary, true, ct);
                        if (item.IsVideo)
                            Interlocked.Decrement(ref _pendingVideo);
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
