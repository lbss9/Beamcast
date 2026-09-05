using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Beamcast.Net;

public sealed class LoungeException : Exception
{
    public LoungeException(string reason, Exception? inner = null)
        : base("Lounge: " + reason, inner)
    {
        Reason = reason;
    }

    /// <summary>One of the LoungeProtocol reasons, or "unreachable" / "timeout" / "protocol".</summary>
    public string Reason { get; }
}

/// <summary>
/// One member's connection to a lounge. Owns the WebSocket, the content key and the single
/// outbound queue. Everything members exchange (presence, stream metadata, media) is sealed with
/// the content key before it leaves this class and opened when it comes back in.
/// </summary>
public sealed class LoungeClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly ClientWebSocket _socket;
    private readonly SecureChannel _channel;
    private readonly Channel<(byte[] Frame, uint StreamId, bool IsVideo)> _outbox =
        Channel.CreateUnbounded<(byte[], uint, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<uint, int> _pendingVideo = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<uint>> _publishWaiters = new();
    private readonly byte[] _scratch = new byte[64 * 1024];
    private CancellationTokenSource? _cts;
    private uint _publishTag;
    private int _closed;

    private LoungeClient(ClientWebSocket socket, SecureChannel channel, string serverUrl, LoungeWelcome welcome)
    {
        _socket = socket;
        _channel = channel;
        ServerUrl = serverUrl;
        Code = welcome.Code ?? string.Empty;
        Name = welcome.Name ?? string.Empty;
        MemberId = welcome.MemberId;
        InitialMembers = welcome.Members;
        InitialStreams = welcome.Streams;
    }

    public string ServerUrl { get; }
    public string Code { get; }
    public string Name { get; }
    public uint MemberId { get; }
    public List<LoungeMemberInfo> InitialMembers { get; }
    public List<LoungeStreamInfo> InitialStreams { get; }
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public event Action<uint>? MemberJoined;
    public event Action<uint>? MemberLeft;
    public event Action<uint, PresenceMessage>? PresenceReceived;
    public event Action<uint, uint, StreamMetaMessage>? StreamStarted;
    public event Action<uint>? StreamEnded;
    /// <summary>Decrypted media: stream id, message type, keyframe flag, plaintext body.</summary>
    public event Action<uint, MessageType, bool, byte[]>? MediaReceived;
    public event Action<uint, StreamMetaMessage>? StreamMetaUpdated;
    public event Action<uint>? KeyframeRequested;
    public event Action<string>? Closed;

    public static Task<LoungeClient> CreateAsync(string serverUrl, string loungeName, string password, string? appKey, CancellationToken ct) =>
        ConnectAsync(serverUrl, appKey, password, ct, request =>
        {
            request.Op = LoungeProtocol.OpCreate;
            request.Name = loungeName.Trim();
        });

    public static Task<LoungeClient> JoinAsync(string serverUrl, string code, string password, string? appKey, CancellationToken ct) =>
        ConnectAsync(serverUrl, appKey, password, ct, request =>
        {
            request.Op = LoungeProtocol.OpJoin;
            request.Code = LoungeProtocol.NormalizeCode(code);
        });

    private static async Task<LoungeClient> ConnectAsync(string serverUrl, string? appKey, string password, CancellationToken ct, Action<LoungeRequest> fill)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);

            var request = new LoungeRequest { AppKey = appKey };
            fill(request);

            byte[] key;
            if (request.Op == LoungeProtocol.OpCreate)
            {
                var salt = LoungeCrypto.NewSalt();
                key = await Task.Run(() => LoungeCrypto.DeriveKey(password, salt), timeout.Token).ConfigureAwait(false);
                request.Salt = Convert.ToBase64String(salt);
                request.Verifier = Convert.ToBase64String(LoungeCrypto.Verifier(key));
                await SendJsonAsync(socket, request, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await SendJsonAsync(socket, request, timeout.Token).ConfigureAwait(false);
                var challenge = await ReadJsonAsync<LoungeChallenge>(socket, timeout.Token).ConfigureAwait(false);
                if (challenge is null)
                    throw new LoungeException("protocol");
                if (!challenge.Ok)
                    throw new LoungeException(challenge.Reason ?? "protocol");
                var salt = Convert.FromBase64String(challenge.Salt ?? string.Empty);
                key = await Task.Run(() => LoungeCrypto.DeriveKey(password, salt), timeout.Token).ConfigureAwait(false);
                var proof = LoungeCrypto.Proof(LoungeCrypto.Verifier(key), challenge.Nonce ?? string.Empty);
                await SendJsonAsync(socket, new LoungeProof { Proof = proof }, timeout.Token).ConfigureAwait(false);
            }

            var welcome = await ReadJsonAsync<LoungeWelcome>(socket, timeout.Token).ConfigureAwait(false);
            if (welcome is null)
                throw new LoungeException("protocol");
            if (!welcome.Ok)
                throw new LoungeException(welcome.Reason ?? "protocol");

            var channel = new SecureChannel(LoungeCrypto.ContentKey(key));
            CryptographicClear(key);
            var client = new LoungeClient(socket, channel, url, welcome);
            client.Start();
            return client;
        }
        catch (LoungeException)
        {
            socket.Dispose();
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new LoungeException("timeout");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidDataException or FormatException)
        {
            socket.Dispose();
            throw new LoungeException("unreachable", ex);
        }
    }

    private static void CryptographicClear(byte[] key) => Array.Clear(key);

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _ = SendLoopAsync(_cts.Token);
        _ = ReceiveLoopAsync(_cts.Token);
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    /// <summary>Opens an opaque blob handed over by the server (presence or stream metadata).</summary>
    public T? Open<T>(string? base64Blob) where T : class
    {
        if (string.IsNullOrEmpty(base64Blob))
            return null;
        try
        {
            var framed = Convert.FromBase64String(base64Blob);
            return _channel.TryOpenFramed(framed, out _, out var plaintext) ? Json.Deserialize<T>(plaintext) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void SendPresence(PresenceMessage presence) =>
        Enqueue(LoungeMux.Encode(LoungeMux.Presence, 0, 0, _channel.Seal(MessageType.Presence, Json.Serialize(presence))));

    /// <summary>Announces a stream; resolves with the server-assigned stream id.</summary>
    public async Task<uint> PublishAsync(StreamMetaMessage meta, CancellationToken ct)
    {
        var tag = Interlocked.Increment(ref _publishTag);
        var tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _publishWaiters[tag] = tcs;
        Enqueue(LoungeMux.Encode(LoungeMux.Publish, tag, 0, _channel.Seal(MessageType.StreamMeta, Json.Serialize(meta))));
        using var registration = ct.Register(() => tcs.TrySetCanceled());
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _publishWaiters.TryRemove(tag, out _);
        }
    }

    public void Unpublish(uint streamId) =>
        Enqueue(LoungeMux.Encode(LoungeMux.Unpublish, streamId, 0, ReadOnlySpan<byte>.Empty));

    /// <summary>Tells everyone about a state change (paused/live) or a new title for a stream.</summary>
    public void UpdateStreamMeta(uint streamId, StreamMetaMessage meta) =>
        Enqueue(LoungeMux.Encode(LoungeMux.Control, streamId, 0, _channel.Seal(MessageType.StreamMeta, Json.Serialize(meta))));

    public void Subscribe(uint streamId) =>
        Enqueue(LoungeMux.Encode(LoungeMux.Subscribe, streamId, 0, ReadOnlySpan<byte>.Empty));

    public void Unsubscribe(uint streamId) =>
        Enqueue(LoungeMux.Encode(LoungeMux.Unsubscribe, streamId, 0, ReadOnlySpan<byte>.Empty));

    public void RequestKeyframe(uint streamId) =>
        Enqueue(LoungeMux.Encode(LoungeMux.KeyframeRequest, streamId, 0, ReadOnlySpan<byte>.Empty));

    /// <summary>Queued video frames for a stream that have not hit the socket yet (for the publisher's own gate).</summary>
    public int PendingVideo(uint streamId) => _pendingVideo.TryGetValue(streamId, out var n) ? n : 0;

    public void SendMedia(uint streamId, MessageType type, ReadOnlySpan<byte> body, bool keyframe)
    {
        var framed = _channel.Seal(type, body, keyframe ? MessageFlags.Keyframe : MessageFlags.None);
        var isVideo = type == MessageType.Video;
        if (isVideo)
            _pendingVideo.AddOrUpdate(streamId, 1, (_, n) => n + 1);
        _outbox.Writer.TryWrite((LoungeMux.Encode(LoungeMux.Media, streamId, 0, framed), streamId, isVideo));
    }

    private void Enqueue(byte[] frame) => _outbox.Writer.TryWrite((frame, 0, false));

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
                        _pendingVideo.AddOrUpdate(item.StreamId, 0, (_, n) => Math.Max(0, n - 1));
                }
            }
        }
        catch (Exception) { }
        finally
        {
            Close("lost");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(LoungeProtocol.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Enqueue(LoungeMux.Encode(LoungeMux.Heartbeat, 0, 0, ReadOnlySpan<byte>.Empty));
        }
        catch (Exception) { }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reason = "closed";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(LoungeProtocol.IdleTimeout);
                    try
                    {
                        frame = await ReadFrameAsync(_socket, _scratch, idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        reason = "timeout";
                        break;
                    }
                }
                if (frame is null)
                    break;
                if (!LoungeMux.TryDecode(frame, out var kind, out var a, out var b, out var payload))
                    continue;
                Dispatch(kind, a, b, payload);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "left";
        }
        catch (Exception)
        {
            reason = "lost";
        }
        Close(reason);
    }

    private void Dispatch(byte kind, uint a, uint b, byte[] payload)
    {
        switch (kind)
        {
            case LoungeMux.MemberJoined:
                MemberJoined?.Invoke(a);
                break;
            case LoungeMux.MemberLeft:
                MemberLeft?.Invoke(a);
                break;
            case LoungeMux.Presence:
                if (_channel.TryOpenFramed(payload, out _, out var presenceBytes) && Json.Deserialize<PresenceMessage>(presenceBytes) is { } presence)
                    PresenceReceived?.Invoke(a, presence);
                break;
            case LoungeMux.Control:
                if (_channel.TryOpenFramed(payload, out var controlType, out var controlBytes) && controlType == MessageType.StreamMeta
                    && Json.Deserialize<StreamMetaMessage>(controlBytes) is { } updated)
                    StreamMetaUpdated?.Invoke(a, updated);
                break;
            case LoungeMux.StreamStarted:
                if (_channel.TryOpenFramed(payload, out _, out var metaBytes) && Json.Deserialize<StreamMetaMessage>(metaBytes) is { } meta)
                    StreamStarted?.Invoke(a, b, meta);
                break;
            case LoungeMux.StreamEnded:
                StreamEnded?.Invoke(a);
                break;
            case LoungeMux.PublishAck:
                if (_publishWaiters.TryRemove(b, out var waiter))
                    waiter.TrySetResult(a);
                break;
            case LoungeMux.KeyframeRequest:
                KeyframeRequested?.Invoke(a);
                break;
            case LoungeMux.Media:
                if (Framing.TryDecodeWhole(payload, out var message) && _channel.TryOpen(message, out var body))
                    MediaReceived?.Invoke(a, message.Type, message.IsKeyframe, body);
                break;
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
                result = await socket.ReceiveAsync(scratch, ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            assembled.Write(scratch, 0, result.Count);
            if (assembled.Length > Framing.MaxMessageSize + LoungeMux.HeaderSize)
                throw new InvalidDataException("Frame too large.");
            if (result.EndOfMessage)
                return assembled.ToArray();
        }
    }

    private static Task SendJsonAsync<T>(WebSocket socket, T value, CancellationToken ct) =>
        socket.SendAsync(Json.Serialize(value), WebSocketMessageType.Text, true, ct);

    private static async Task<T?> ReadJsonAsync<T>(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var assembled = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return default;
            assembled.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        return Json.Deserialize<T>(Encoding.UTF8.GetString(assembled.ToArray()));
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        _outbox.Writer.TryComplete();
        foreach (var waiter in _publishWaiters.Values)
            waiter.TrySetCanceled();
        SafeTry.Run(() => _cts?.Cancel());
        SafeTry.Run(() => _socket.Abort());
        Closed?.Invoke(reason);
    }

    public async Task LeaveAsync()
    {
        if (!IsOpen)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(1000);
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
        Close("left");
    }

    public void Dispose()
    {
        Close("disposed");
        SafeTry.Run(() => _socket.Dispose());
        _cts?.Dispose();
        _channel.Dispose();
    }
}
