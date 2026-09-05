using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
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

    /// <summary>One of the LoungeProtocol reasons, or "unreachable" / "timeout" / "protocol" / "password_required".</summary>
    public string Reason { get; }

    public const string PasswordRequired = "password_required";
}

public sealed class RoomCreateOptions
{
    public string Name { get; set; } = string.Empty;
    public string Visibility { get; set; } = RoomVisibility.Private;
    public string Kind { get; set; } = RoomKind.Permanent;
    public double TtlHours { get; set; } = LoungeProtocol.DefaultTtlHours;
    public string Password { get; set; } = string.Empty;
    public string Broadcast { get; set; } = BroadcastPolicy.Everyone;
    public int MaxMembers { get; set; }
}

public sealed class RoomJoinOptions
{
    public string Code { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>The stretched password key from an earlier session, so a reconnect needs no password.</summary>
    public byte[]? PasswordKey { get; set; }

    public string? InviteToken { get; set; }
    public byte[]? InviteKey { get; set; }
    public string? OwnerToken { get; set; }
}

/// <summary>
/// One member's connection to a room. Owns the WebSocket, the content key and the single outbound
/// queue. Everything members exchange (presence, stream metadata, media) is sealed with the content
/// key before it leaves this class and opened when it comes back in. In rooms without a password
/// the key arrives from a member already inside, wrapped for this client's ephemeral ECDH key.
/// </summary>
public sealed class LoungeClient : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const int MaxBufferedBeforeKey = 512;

    private readonly ClientWebSocket _socket;
    private readonly Channel<(byte[] Frame, uint StreamId, bool IsVideo)> _outbox =
        Channel.CreateUnbounded<(byte[], uint, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<uint, int> _pendingVideo = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<uint>> _publishWaiters = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<InviteCreatedMessage>> _inviteWaiters = new();
    private readonly TaskCompletionSource<bool> _keyReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(byte Kind, uint A, uint B, byte[] Payload)> _beforeKey = [];
    private readonly byte[] _scratch = new byte[64 * 1024];
    private readonly object _keySync = new();
    private SecureChannel? _channel;
    private byte[]? _contentKey;
    private ECDiffieHellman? _ecdh;
    private byte[]? _joinPublicKey;
    private CancellationTokenSource? _cts;
    private uint _tag;
    private int _closed;
    private string? _byeReason;

    private LoungeClient(ClientWebSocket socket, string serverUrl, LoungeReply welcome)
    {
        _socket = socket;
        ServerUrl = serverUrl;
        Room = welcome.Room ?? new RoomInfo();
        MemberId = welcome.MemberId;
        IsOwner = welcome.IsOwner;
        OwnerToken = welcome.OwnerToken;
        InitialMembers = welcome.Members;
        InitialStreams = welcome.Streams;
    }

    public string ServerUrl { get; }
    public RoomInfo Room { get; private set; }
    public string Code => Room.Code;
    public string Name => Room.Name;
    public uint MemberId { get; }
    public bool IsOwner { get; }

    /// <summary>Only set right after creating the room; store it to manage the room later.</summary>
    public string? OwnerToken { get; }

    public List<LoungeMemberInfo> InitialMembers { get; }
    public List<LoungeStreamInfo> InitialStreams { get; }
    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>The stretched password key (password rooms), reusable for a reconnect.</summary>
    public byte[]? PasswordKey { get; private set; }

    /// <summary>Copy of the room content key, for invites that skip the password.</summary>
    public byte[]? ContentKey
    {
        get
        {
            lock (_keySync)
                return _contentKey is null ? null : (byte[])_contentKey.Clone();
        }
    }

    public event Action<uint, bool>? MemberJoined;
    public event Action<uint>? MemberLeft;
    public event Action<uint, PresenceMessage>? PresenceReceived;
    public event Action<uint, uint, StreamMetaMessage>? StreamStarted;
    public event Action<uint>? StreamEnded;
    /// <summary>Decrypted media: stream id, message type, keyframe flag, plaintext body.</summary>
    public event Action<uint, MessageType, bool, byte[]>? MediaReceived;
    public event Action<uint, StreamMetaMessage>? StreamMetaUpdated;
    public event Action<uint>? KeyframeRequested;
    public event Action<RoomInfo>? RoomUpdated;
    public event Action<string>? Notice;
    public event Action<string>? Closed;

    // ----- discovery -----

    public static async Task<HostInfo> ListRoomsAsync(string serverUrl, string? appKey, CancellationToken ct)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LoungeProtocol.HttpUrl(url, LoungeProtocol.RoomsPath));
            if (!string.IsNullOrEmpty(appKey))
                request.Headers.TryAddWithoutValidation(LoungeProtocol.AppKeyHeader, appKey);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                throw new LoungeException(LoungeProtocol.ReasonBadKey);
            if (!response.IsSuccessStatusCode)
                throw new LoungeException("unreachable");
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var info = Json.Deserialize<HostInfo>(body) ?? throw new LoungeException("protocol");
            if (info.Protocol != LoungeProtocol.Version)
                throw new LoungeException(LoungeProtocol.ReasonVersion);
            return info;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LoungeException("timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            throw new LoungeException("unreachable", ex);
        }
    }

    // ----- connect -----

    public static async Task<LoungeClient> CreateAsync(string serverUrl, RoomCreateOptions options, string? appKey, CancellationToken ct)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);

        var socket = NewSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);

            var request = new LoungeRequest
            {
                Op = LoungeProtocol.OpCreate,
                AppKey = appKey,
                Name = options.Name.Trim(),
                Visibility = RoomVisibility.Normalize(options.Visibility),
                Kind = RoomKind.Normalize(options.Kind),
                TtlHours = options.TtlHours,
                Broadcast = BroadcastPolicy.Normalize(options.Broadcast),
                MaxMembers = options.MaxMembers,
            };

            byte[] contentKey;
            byte[]? passwordKey = null;
            if (options.Password.Length > 0)
            {
                var salt = LoungeCrypto.NewSalt();
                passwordKey = await Task.Run(() => LoungeCrypto.DeriveKey(options.Password, salt), timeout.Token).ConfigureAwait(false);
                request.Salt = Convert.ToBase64String(salt);
                request.Verifier = Convert.ToBase64String(LoungeCrypto.Verifier(passwordKey));
                contentKey = LoungeCrypto.ContentKey(passwordKey);
            }
            else
            {
                contentKey = LoungeCrypto.NewRoomKey();
            }

            await SendJsonAsync(socket, request, timeout.Token).ConfigureAwait(false);
            var welcome = await ReadJsonAsync<LoungeReply>(socket, timeout.Token).ConfigureAwait(false) ?? throw new LoungeException("protocol");
            if (!welcome.Ok)
                throw new LoungeException(welcome.Reason ?? "protocol");

            var client = new LoungeClient(socket, url, welcome) { PasswordKey = passwordKey };
            client.SetKey(contentKey);
            client.Start();
            return client;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw Translate(ex, ct);
        }
    }

    public static async Task<LoungeClient> JoinAsync(string serverUrl, RoomJoinOptions options, string? appKey, CancellationToken ct)
    {
        if (!LoungeProtocol.TryNormalizeServer(serverUrl, out var url))
            throw new LoungeException(LoungeProtocol.ReasonBadRequest);

        var socket = NewSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            await socket.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);

            var joinPublic = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
            var request = new LoungeRequest
            {
                Op = LoungeProtocol.OpJoin,
                AppKey = appKey,
                Code = LoungeProtocol.NormalizeCode(options.Code),
                Invite = options.InviteToken,
                OwnerToken = options.OwnerToken,
                JoinKey = Convert.ToBase64String(joinPublic),
            };
            await SendJsonAsync(socket, request, timeout.Token).ConfigureAwait(false);

            var reply = await ReadJsonAsync<LoungeReply>(socket, timeout.Token).ConfigureAwait(false) ?? throw new LoungeException("protocol");
            if (!reply.Ok)
                throw new LoungeException(reply.Reason ?? "protocol");

            byte[]? passwordKey = options.PasswordKey;
            if (reply.Stage == LoungeReply.StageChallenge)
            {
                if (passwordKey is null)
                {
                    if (options.Password.Length == 0)
                        throw new LoungeException(LoungeException.PasswordRequired);
                    var salt = Convert.FromBase64String(reply.Salt ?? string.Empty);
                    passwordKey = await Task.Run(() => LoungeCrypto.DeriveKey(options.Password, salt), timeout.Token).ConfigureAwait(false);
                }
                var proof = LoungeCrypto.Proof(LoungeCrypto.Verifier(passwordKey), reply.Nonce ?? string.Empty);
                await SendJsonAsync(socket, new LoungeProof { Proof = proof }, timeout.Token).ConfigureAwait(false);
                reply = await ReadJsonAsync<LoungeReply>(socket, timeout.Token).ConfigureAwait(false) ?? throw new LoungeException("protocol");
                if (!reply.Ok)
                    throw new LoungeException(reply.Reason ?? "protocol");
            }
            if (reply.Stage != LoungeReply.StageWelcome)
                throw new LoungeException("protocol");

            var client = new LoungeClient(socket, url, reply) { PasswordKey = passwordKey };
            if (reply.NeedsKey)
            {
                client._ecdh = ecdh;
                client._joinPublicKey = joinPublic;
                ecdh = null;
            }
            else if (options.InviteKey is { Length: LoungeCrypto.KeyBytes })
            {
                client.SetKey((byte[])options.InviteKey.Clone());
            }
            else if (passwordKey is not null)
            {
                client.SetKey(LoungeCrypto.ContentKey(passwordKey));
            }
            else
            {
                client.SetKey(LoungeCrypto.NewRoomKey());
            }

            client.Start();
            if (reply.NeedsKey)
            {
                using var handoff = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handoff.CancelAfter(LoungeProtocol.KeyHandoffTimeout);
                try
                {
                    await client._keyReady.Task.WaitAsync(handoff.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    client.Close(LoungeProtocol.ReasonNoKey);
                    client.Dispose();
                    throw new LoungeException(client._byeReason ?? LoungeProtocol.ReasonNoKey);
                }
                if (!client.IsOpen)
                    throw new LoungeException(client._byeReason ?? "closed");
            }
            return client;
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw Translate(ex, ct);
        }
        finally
        {
            ecdh?.Dispose();
        }
    }

    private static ClientWebSocket NewSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return socket;
    }

    private static Exception Translate(Exception ex, CancellationToken ct) => ex switch
    {
        LoungeException => ex,
        OperationCanceledException when !ct.IsCancellationRequested => new LoungeException("timeout"),
        OperationCanceledException => ex,
        WebSocketException or IOException or InvalidDataException or FormatException or System.Text.Json.JsonException => new LoungeException("unreachable", ex),
        _ => ex,
    };

    private void SetKey(byte[] key)
    {
        List<(byte Kind, uint A, uint B, byte[] Payload)> replay;
        lock (_keySync)
        {
            _channel?.Dispose();
            _channel = new SecureChannel(key);
            _contentKey = key;
            replay = _beforeKey.ToList();
            _beforeKey.Clear();
        }
        _ecdh?.Dispose();
        _ecdh = null;
        _keyReady.TrySetResult(true);
        foreach (var frame in replay)
            Dispatch(frame.Kind, frame.A, frame.B, frame.Payload);
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _ = SendLoopAsync(_cts.Token);
        _ = ReceiveLoopAsync(_cts.Token);
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    private SecureChannel Sealer => _channel ?? throw new InvalidOperationException("No room key yet.");

    /// <summary>Opens an opaque blob handed over by the server (presence or stream metadata).</summary>
    public T? Open<T>(string? base64Blob) where T : class
    {
        if (string.IsNullOrEmpty(base64Blob) || _channel is null)
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
        Enqueue(LoungeMux.Encode(LoungeMux.Presence, 0, 0, Sealer.Seal(MessageType.Presence, Json.Serialize(presence))));

    /// <summary>Announces a stream; resolves with the server-assigned stream id.</summary>
    public async Task<uint> PublishAsync(StreamMetaMessage meta, CancellationToken ct)
    {
        var tag = Interlocked.Increment(ref _tag);
        var tcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _publishWaiters[tag] = tcs;
        Enqueue(LoungeMux.Encode(LoungeMux.Publish, tag, 0, Sealer.Seal(MessageType.StreamMeta, Json.Serialize(meta))));
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
        Enqueue(LoungeMux.Encode(LoungeMux.Control, streamId, 0, Sealer.Seal(MessageType.StreamMeta, Json.Serialize(meta))));

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
        var channel = _channel;
        if (channel is null)
            return;
        var framed = channel.Seal(type, body, keyframe ? MessageFlags.Keyframe : MessageFlags.None);
        var isVideo = type == MessageType.Video;
        if (isVideo)
            _pendingVideo.AddOrUpdate(streamId, 1, (_, n) => n + 1);
        _outbox.Writer.TryWrite((LoungeMux.Encode(LoungeMux.Media, streamId, 0, framed), streamId, isVideo));
    }

    // ----- owner operations -----

    public async Task<InviteCreatedMessage> CreateInviteAsync(TimeSpan? expiresIn, int maxUses, CancellationToken ct)
    {
        var tag = Interlocked.Increment(ref _tag);
        var tcs = new TaskCompletionSource<InviteCreatedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inviteWaiters[tag] = tcs;
        var request = new InviteRequestMessage
        {
            ExpiresInSeconds = expiresIn is { } span ? (long)span.TotalSeconds : 0,
            MaxUses = maxUses,
        };
        Enqueue(LoungeMux.Encode(LoungeMux.InviteCreate, tag, 0, Json.Serialize(request)));
        using var registration = ct.Register(() => tcs.TrySetCanceled());
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _inviteWaiters.TryRemove(tag, out _);
        }
    }

    public void RevokeInvites() => Enqueue(LoungeMux.Encode(LoungeMux.InviteRevokeAll, 0, 0, ReadOnlySpan<byte>.Empty));

    public void Kick(uint memberId) => Enqueue(LoungeMux.Encode(LoungeMux.Kick, memberId, 0, ReadOnlySpan<byte>.Empty));

    public void DeleteRoom() => Enqueue(LoungeMux.Encode(LoungeMux.RoomDelete, 0, 0, ReadOnlySpan<byte>.Empty));

    public void UpdateRoom(RoomUpdateMessage update) =>
        Enqueue(LoungeMux.Encode(LoungeMux.RoomUpdate, 0, 0, Json.Serialize(update)));

    /// <summary>Sets, changes or (empty) removes the password. Other members are asked to rejoin on a change.</summary>
    public async Task ChangePasswordAsync(string newPassword, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            UpdateRoom(new RoomUpdateMessage { ClearPassword = true });
            PasswordKey = null;
            return;
        }
        var salt = LoungeCrypto.NewSalt();
        var key = await Task.Run(() => LoungeCrypto.DeriveKey(newPassword, salt), ct).ConfigureAwait(false);
        UpdateRoom(new RoomUpdateMessage
        {
            Salt = Convert.ToBase64String(salt),
            Verifier = Convert.ToBase64String(LoungeCrypto.Verifier(key)),
        });
        PasswordKey = key;
        SetKey(LoungeCrypto.ContentKey(key));
    }

    // ----- loops -----

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
            Close(_byeReason ?? "lost");
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
                Route(kind, a, b, payload);
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
        Close(_byeReason ?? reason);
    }

    private void Route(byte kind, uint a, uint b, byte[] payload)
    {
        switch (kind)
        {
            case LoungeMux.Heartbeat:
                return;
            case LoungeMux.Bye:
                _byeReason = Json.Deserialize<ServerNotice>(payload)?.Reason ?? "closed";
                return;
            case LoungeMux.KeyGrant:
                OnKeyGrant(payload);
                return;
            case LoungeMux.KeyRequest:
                OnKeyRequest(a, payload);
                return;
        }

        if (_channel is null)
        {
            lock (_keySync)
            {
                if (_channel is null)
                {
                    if (_beforeKey.Count < MaxBufferedBeforeKey)
                        _beforeKey.Add((kind, a, b, payload));
                    return;
                }
            }
        }
        Dispatch(kind, a, b, payload);
    }

    private void OnKeyGrant(byte[] payload)
    {
        if (_channel is not null)
            return;
        if (payload.Length == 0)
        {
            // Nobody inside held a key any more: this client starts a fresh one.
            SetKey(LoungeCrypto.NewRoomKey());
            return;
        }
        var ecdh = _ecdh;
        var joinPublic = _joinPublicKey;
        if (ecdh is null || joinPublic is null)
            return;
        if (LoungeCrypto.TryUnwrapRoomKey(ecdh, joinPublic, payload, out var key))
            SetKey(key);
    }

    private void OnKeyRequest(uint newcomerId, byte[] newcomerPublicKey)
    {
        byte[]? key;
        lock (_keySync)
            key = _contentKey is null ? null : (byte[])_contentKey.Clone();
        if (key is null || newcomerPublicKey.Length == 0)
            return;
        try
        {
            var wrapped = LoungeCrypto.WrapRoomKey(key, newcomerPublicKey);
            Enqueue(LoungeMux.Encode(LoungeMux.KeyGrant, newcomerId, 0, wrapped));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private void Dispatch(byte kind, uint a, uint b, byte[] payload)
    {
        var channel = _channel;
        switch (kind)
        {
            case LoungeMux.MemberJoined:
                MemberJoined?.Invoke(a, b == 1);
                break;
            case LoungeMux.MemberLeft:
                MemberLeft?.Invoke(a);
                break;
            case LoungeMux.Presence:
                if (channel is not null && channel.TryOpenFramed(payload, out _, out var presenceBytes) && Json.Deserialize<PresenceMessage>(presenceBytes) is { } presence)
                    PresenceReceived?.Invoke(a, presence);
                break;
            case LoungeMux.Control:
                if (channel is not null && channel.TryOpenFramed(payload, out var controlType, out var controlBytes) && controlType == MessageType.StreamMeta
                    && Json.Deserialize<StreamMetaMessage>(controlBytes) is { } updated)
                    StreamMetaUpdated?.Invoke(a, updated);
                break;
            case LoungeMux.StreamStarted:
                if (channel is not null && channel.TryOpenFramed(payload, out _, out var metaBytes) && Json.Deserialize<StreamMetaMessage>(metaBytes) is { } meta)
                    StreamStarted?.Invoke(a, b, meta);
                break;
            case LoungeMux.StreamEnded:
                StreamEnded?.Invoke(a);
                break;
            case LoungeMux.PublishAck:
                if (_publishWaiters.TryRemove(b, out var waiter))
                {
                    if (a == 0)
                        waiter.TrySetException(new LoungeException(LoungeProtocol.ReasonNotAllowed));
                    else
                        waiter.TrySetResult(a);
                }
                break;
            case LoungeMux.KeyframeRequest:
                KeyframeRequested?.Invoke(a);
                break;
            case LoungeMux.Media:
                if (channel is not null && Framing.TryDecodeWhole(payload, out var message) && channel.TryOpen(message, out var body))
                    MediaReceived?.Invoke(a, message.Type, message.IsKeyframe, body);
                break;
            case LoungeMux.RoomInfo:
                if (Json.Deserialize<RoomInfo>(payload) is { } info)
                {
                    Room = info;
                    RoomUpdated?.Invoke(info);
                }
                break;
            case LoungeMux.InviteCreated:
                if (_inviteWaiters.TryRemove(b, out var inviteWaiter) && Json.Deserialize<InviteCreatedMessage>(payload) is { } created)
                    inviteWaiter.TrySetResult(created);
                break;
            case LoungeMux.Notice:
                if (Json.Deserialize<ServerNotice>(payload) is { } notice)
                {
                    if (_inviteWaiters.TryRemove(a, out var failed))
                        failed.TrySetException(new LoungeException(notice.Reason));
                    Notice?.Invoke(notice.Reason);
                }
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
        foreach (var waiter in _inviteWaiters.Values)
            waiter.TrySetCanceled();
        _keyReady.TrySetResult(false);
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
        _ecdh?.Dispose();
        lock (_keySync)
        {
            _channel?.Dispose();
            _channel = null;
            if (_contentKey is not null)
                CryptographicOperations.ZeroMemory(_contentKey);
            _contentKey = null;
        }
    }
}
