using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Beamcast.Net;

namespace Beamcast.Server;

/// <summary>
/// Lounges, their members and their streams. The server is deliberately blind: it checks
/// password proofs against a verifier, forwards opaque encrypted blobs, and routes media from a
/// publisher to whoever subscribed, applying a per-subscriber keyframe gate so a slow member never
/// stalls anyone else. It cannot read names, titles or pictures.
/// </summary>
public sealed class LoungeHub
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, Lounge> _lounges = new(StringComparer.Ordinal);
    private readonly ServerOptions _options;
    private readonly LoungeStore _store;
    private readonly ILogger<LoungeHub> _log;

    public LoungeHub(ServerOptions options, LoungeStore store, ILogger<LoungeHub> log)
    {
        _options = options;
        _store = store;
        _log = log;
    }

    public object Snapshot() => new
    {
        lounges = _lounges.Count,
        members = _lounges.Values.Sum(l => l.MemberCount),
        streams = _lounges.Values.Sum(l => l.StreamCount),
        uptimeSeconds = Environment.TickCount64 / 1000,
    };

    public void LoadPersisted()
    {
        foreach (var record in _store.Load())
        {
            try
            {
                var lounge = new Lounge(record.Code, record.Name, Convert.FromBase64String(record.Salt), Convert.FromBase64String(record.Verifier), record.CreatedAt, _log)
                {
                    LastActiveAt = record.LastActiveAt,
                };
                _lounges.TryAdd(lounge.Code, lounge);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping malformed lounge record {Code}.", record.Code);
            }
        }
        _log.LogInformation("{Count} lounge(s) restored.", _lounges.Count);
    }

    private void Persist() =>
        _store.Save(_lounges.Values.Select(l => new LoungeRecord
        {
            Code = l.Code,
            Name = l.Name,
            Salt = Convert.ToBase64String(l.Salt),
            Verifier = Convert.ToBase64String(l.Verifier),
            CreatedAt = l.CreatedAt,
            LastActiveAt = l.LastActiveAt,
        }));

    /// <summary>Drops lounges that have been empty longer than the configured TTL.</summary>
    public void Sweep()
    {
        if (_options.EmptyLoungeTtl <= TimeSpan.Zero)
            return;
        var cutoff = DateTimeOffset.UtcNow - _options.EmptyLoungeTtl;
        var removed = 0;
        foreach (var lounge in _lounges.Values.Where(l => l.MemberCount == 0 && l.LastActiveAt < cutoff).ToList())
        {
            if (_lounges.TryRemove(lounge.Code, out _))
                removed++;
        }
        if (removed > 0)
        {
            _log.LogInformation("Swept {Count} empty lounge(s).", removed);
            Persist();
        }
    }

    public async Task HandleAsync(WebSocket socket, string remote, CancellationToken ct)
    {
        LoungeRequest? request;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            request = await ReadJsonAsync<LoungeRequest>(socket, timeout.Token);
        }
        catch (Exception)
        {
            return;
        }

        if (request is null)
        {
            await SendJsonAsync(socket, new LoungeWelcome { Ok = false, Reason = LoungeProtocol.ReasonBadRequest }, ct);
            return;
        }
        if (request.Version != LoungeProtocol.Version)
        {
            await SendJsonAsync(socket, new LoungeWelcome { Ok = false, Reason = LoungeProtocol.ReasonVersion }, ct);
            return;
        }
        if (!KeyMatches(request.AppKey))
        {
            _log.LogWarning("Refused {Remote}: bad app key.", remote);
            await SendJsonAsync(socket, new LoungeWelcome { Ok = false, Reason = LoungeProtocol.ReasonBadKey }, ct);
            return;
        }

        Lounge? lounge;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            lounge = request.Op == LoungeProtocol.OpCreate
                ? await CreateAsync(socket, request, remote, timeout.Token)
                : await JoinAsync(socket, request, remote, timeout.Token);
        }
        catch (Exception)
        {
            return;
        }

        if (lounge is null)
            return;

        await lounge.RunMemberAsync(socket, remote, ct);
        lounge.LastActiveAt = DateTimeOffset.UtcNow;
    }

    private bool KeyMatches(string? presented)
    {
        if (string.IsNullOrEmpty(_options.AppKey))
            return true;
        var expected = Encoding.UTF8.GetBytes(_options.AppKey);
        var actual = Encoding.UTF8.GetBytes(presented ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private async Task<Lounge?> CreateAsync(WebSocket socket, LoungeRequest request, string remote, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        byte[] salt;
        byte[] verifier;
        try
        {
            salt = Convert.FromBase64String(request.Salt ?? string.Empty);
            verifier = Convert.FromBase64String(request.Verifier ?? string.Empty);
        }
        catch (FormatException)
        {
            salt = [];
            verifier = [];
        }

        if (name.Length == 0 || name.Length > LoungeProtocol.MaxNameLength || salt.Length != LoungeCrypto.SaltBytes || verifier.Length != 32)
        {
            await SendJsonAsync(socket, new LoungeWelcome { Ok = false, Reason = LoungeProtocol.ReasonBadRequest }, ct);
            return null;
        }

        Lounge lounge;
        while (true)
        {
            lounge = new Lounge(LoungeProtocol.NewCode(), name, salt, verifier, DateTimeOffset.UtcNow, _log);
            if (_lounges.TryAdd(lounge.Code, lounge))
                break;
        }
        Persist();
        _log.LogInformation("Lounge {Code} \"{Name}\" created by {Remote}.", lounge.Code, name, remote);
        return lounge;
    }

    private async Task<Lounge?> JoinAsync(WebSocket socket, LoungeRequest request, string remote, CancellationToken ct)
    {
        var code = LoungeProtocol.NormalizeCode(request.Code);
        if (!_lounges.TryGetValue(code, out var lounge))
        {
            await SendJsonAsync(socket, new LoungeChallenge { Ok = false, Reason = LoungeProtocol.ReasonNoLounge }, ct);
            return null;
        }

        var nonce = LoungeCrypto.NewNonce();
        await SendJsonAsync(socket, new LoungeChallenge
        {
            Ok = true,
            Code = lounge.Code,
            Name = lounge.Name,
            Salt = Convert.ToBase64String(lounge.Salt),
            Nonce = nonce,
        }, ct);

        var proof = await ReadJsonAsync<LoungeProof>(socket, ct);
        if (proof is null || !LoungeCrypto.VerifyProof(lounge.Verifier, nonce, proof.Proof))
        {
            _log.LogWarning("Wrong password for lounge {Code} from {Remote}.", lounge.Code, remote);
            await SendJsonAsync(socket, new LoungeWelcome { Ok = false, Reason = LoungeProtocol.ReasonBadPassword }, ct);
            return null;
        }

        return lounge;
    }

    internal static async Task<T?> ReadJsonAsync<T>(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var assembled = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return default;
            assembled.Write(buffer, 0, result.Count);
            if (assembled.Length > 256 * 1024)
                return default;
            if (result.EndOfMessage)
                break;
        }
        return Json.Deserialize<T>(Encoding.UTF8.GetString(assembled.ToArray()));
    }

    internal static Task SendJsonAsync<T>(WebSocket socket, T value, CancellationToken ct) =>
        socket.State == WebSocketState.Open
            ? socket.SendAsync(Json.Serialize(value), WebSocketMessageType.Text, true, ct)
            : Task.CompletedTask;
}

/// <summary>Periodically drops lounges nobody has used for a long time (when a TTL is configured).</summary>
public sealed class LoungeJanitor : BackgroundService
{
    private readonly LoungeHub _hub;

    public LoungeJanitor(LoungeHub hub)
    {
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            _hub.Sweep();
    }
}

/// <summary>One lounge: members, streams, and the routing between them.</summary>
internal sealed class Lounge
{
    private const int MaxPendingFramesPerSubscriber = 4;

    private readonly ConcurrentDictionary<uint, Member> _members = new();
    private readonly ConcurrentDictionary<uint, LoungeStream> _streams = new();
    private readonly ILogger _log;
    private uint _nextMemberId;
    private uint _nextStreamId;

    public Lounge(string code, string name, byte[] salt, byte[] verifier, DateTimeOffset createdAt, ILogger log)
    {
        Code = code;
        Name = name;
        Salt = salt;
        Verifier = verifier;
        CreatedAt = createdAt;
        LastActiveAt = createdAt;
        _log = log;
    }

    public string Code { get; }
    public string Name { get; }
    public byte[] Salt { get; }
    public byte[] Verifier { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActiveAt { get; set; }
    public int MemberCount => _members.Count;
    public int StreamCount => _streams.Count;

    public async Task RunMemberAsync(WebSocket socket, string remote, CancellationToken ct)
    {
        var member = new Member(Interlocked.Increment(ref _nextMemberId), socket);
        _members[member.Id] = member;
        LastActiveAt = DateTimeOffset.UtcNow;

        var welcome = new LoungeWelcome
        {
            Ok = true,
            Code = Code,
            Name = Name,
            MemberId = member.Id,
            Members = _members.Values.Where(m => m.Id != member.Id)
                .Select(m => new LoungeMemberInfo { Id = m.Id, Presence = m.Presence is null ? null : Convert.ToBase64String(m.Presence) })
                .ToList(),
            Streams = _streams.Values
                .Select(s => new LoungeStreamInfo { Id = s.Id, Owner = s.Owner, Meta = Convert.ToBase64String(s.Meta) })
                .ToList(),
        };

        try
        {
            await LoungeHub.SendJsonAsync(socket, welcome, ct);
        }
        catch (Exception)
        {
            _members.TryRemove(member.Id, out _);
            return;
        }

        _log.LogInformation("Member {Id} from {Remote} entered lounge {Code} ({Count} online).", member.Id, remote, Code, _members.Count);
        Broadcast(LoungeMux.Encode(LoungeMux.MemberJoined, member.Id, 0, ReadOnlySpan<byte>.Empty), except: member.Id);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sender = member.SendLoopAsync(linked.Token);
        var scratch = new byte[64 * 1024];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                byte[]? frame;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
                {
                    idle.CancelAfter(LoungeProtocol.IdleTimeout);
                    try
                    {
                        frame = await ReadFrameAsync(socket, scratch, idle.Token);
                    }
                    catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                    {
                        _log.LogInformation("Member {Id} in lounge {Code} timed out.", member.Id, Code);
                        break;
                    }
                }
                if (frame is null)
                    break;
                if (!LoungeMux.TryDecode(frame, out var kind, out var a, out var b, out var payload))
                    continue;
                Handle(member, kind, a, b, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Member {Id} loop ended.", member.Id);
        }
        finally
        {
            linked.Cancel();
            Leave(member);
            member.Complete();
            await SafeAwait(sender);
            await SafeCloseAsync(socket);
            _log.LogInformation("Member {Id} left lounge {Code} ({Count} online).", member.Id, Code, _members.Count);
        }
    }

    private void Handle(Member member, byte kind, uint a, uint b, byte[] payload)
    {
        switch (kind)
        {
            case LoungeMux.Heartbeat:
                member.Enqueue(LoungeMux.Encode(LoungeMux.Heartbeat, 0, 0, ReadOnlySpan<byte>.Empty), 0);
                break;
            case LoungeMux.Presence:
                member.Presence = payload;
                Broadcast(LoungeMux.Encode(LoungeMux.Presence, member.Id, 0, payload), except: member.Id);
                break;

            case LoungeMux.Control:
                Broadcast(LoungeMux.Encode(LoungeMux.Control, member.Id, 0, payload), except: member.Id);
                break;

            case LoungeMux.Publish:
            {
                var stream = new LoungeStream(Interlocked.Increment(ref _nextStreamId), member.Id, payload);
                _streams[stream.Id] = stream;
                member.Enqueue(LoungeMux.Encode(LoungeMux.PublishAck, stream.Id, a, ReadOnlySpan<byte>.Empty), 0);
                Broadcast(LoungeMux.Encode(LoungeMux.StreamStarted, stream.Id, member.Id, payload), except: member.Id);
                _log.LogInformation("Member {Id} publishes stream {Stream} in {Code}.", member.Id, stream.Id, Code);
                break;
            }

            case LoungeMux.Unpublish:
                if (_streams.TryGetValue(a, out var ending) && ending.Owner == member.Id)
                    EndStream(ending);
                break;

            case LoungeMux.Media:
                if (_streams.TryGetValue(a, out var stream2) && stream2.Owner == member.Id)
                    RouteMedia(stream2, payload);
                break;

            case LoungeMux.Subscribe:
                if (_streams.TryGetValue(a, out var target) && target.Owner != member.Id)
                {
                    member.Subscribe(target.Id, MaxPendingFramesPerSubscriber);
                    target.Subscribers.TryAdd(member.Id, 0);
                    RequestKeyframe(target);
                }
                break;

            case LoungeMux.Unsubscribe:
                member.Unsubscribe(a);
                if (_streams.TryGetValue(a, out var left))
                    left.Subscribers.TryRemove(member.Id, out _);
                break;

            case LoungeMux.KeyframeRequest:
                if (_streams.TryGetValue(a, out var wanted) && wanted.Subscribers.ContainsKey(member.Id))
                {
                    member.ResetGate(a);
                    RequestKeyframe(wanted);
                }
                break;
        }
    }

    private void RouteMedia(LoungeStream stream, byte[] framed)
    {
        Framing.TryPeek(framed, out var type, out var flags);
        var isVideo = type == MessageType.Video;
        var keyframe = (flags & MessageFlags.Keyframe) != 0;
        var needKeyframe = false;
        var outbound = LoungeMux.Encode(LoungeMux.Media, stream.Id, 0, framed);

        foreach (var subscriberId in stream.Subscribers.Keys)
        {
            if (!_members.TryGetValue(subscriberId, out var subscriber))
            {
                stream.Subscribers.TryRemove(subscriberId, out _);
                continue;
            }
            if (isVideo)
            {
                if (subscriber.OfferVideo(stream.Id, outbound, keyframe))
                    needKeyframe = true;
            }
            else
            {
                subscriber.Enqueue(outbound, 0);
            }
        }

        if (needKeyframe)
            RequestKeyframe(stream);
    }

    private void RequestKeyframe(LoungeStream stream)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref stream.LastKeyframeRequestTicks);
        if (now - last < 250)
            return;
        Interlocked.Exchange(ref stream.LastKeyframeRequestTicks, now);
        if (_members.TryGetValue(stream.Owner, out var owner))
            owner.Enqueue(LoungeMux.Encode(LoungeMux.KeyframeRequest, stream.Id, 0, ReadOnlySpan<byte>.Empty), 0);
    }

    private void EndStream(LoungeStream stream)
    {
        if (!_streams.TryRemove(stream.Id, out _))
            return;
        foreach (var subscriberId in stream.Subscribers.Keys)
        {
            if (_members.TryGetValue(subscriberId, out var subscriber))
                subscriber.Unsubscribe(stream.Id);
        }
        Broadcast(LoungeMux.Encode(LoungeMux.StreamEnded, stream.Id, stream.Owner, ReadOnlySpan<byte>.Empty), except: null);
    }

    private void Leave(Member member)
    {
        if (!_members.TryRemove(member.Id, out _))
            return;
        foreach (var stream in _streams.Values.Where(s => s.Owner == member.Id).ToList())
            EndStream(stream);
        foreach (var stream in _streams.Values)
            stream.Subscribers.TryRemove(member.Id, out _);
        Broadcast(LoungeMux.Encode(LoungeMux.MemberLeft, member.Id, 0, ReadOnlySpan<byte>.Empty), except: null);
        LastActiveAt = DateTimeOffset.UtcNow;
    }

    private void Broadcast(byte[] frame, uint? except)
    {
        foreach (var member in _members.Values)
        {
            if (except is { } id && member.Id == id)
                continue;
            member.Enqueue(frame, 0);
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
            if (assembled.Length > Framing.MaxMessageSize + LoungeMux.HeaderSize)
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

    /// <summary>A published stream: owner, opaque metadata and who listens.</summary>
    private sealed class LoungeStream
    {
        public LoungeStream(uint id, uint owner, byte[] meta)
        {
            Id = id;
            Owner = owner;
            Meta = meta;
        }

        public uint Id { get; }
        public uint Owner { get; }
        public byte[] Meta { get; }
        public ConcurrentDictionary<uint, byte> Subscribers { get; } = new();
        public long LastKeyframeRequestTicks;
    }

    /// <summary>A connected member: one outbox, one keyframe gate per stream it watches.</summary>
    private sealed class Member
    {
        private readonly Channel<(byte[] Bytes, uint StreamId)> _outbox =
            Channel.CreateUnbounded<(byte[], uint)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly ConcurrentDictionary<uint, Subscription> _subscriptions = new();

        public Member(uint id, WebSocket socket)
        {
            Id = id;
            Socket = socket;
        }

        public uint Id { get; }
        public WebSocket Socket { get; }
        public byte[]? Presence { get; set; }

        public void Subscribe(uint streamId, int maxPending) => _subscriptions[streamId] = new Subscription(maxPending);

        public void Unsubscribe(uint streamId) => _subscriptions.TryRemove(streamId, out _);

        public void ResetGate(uint streamId)
        {
            if (_subscriptions.TryGetValue(streamId, out var sub))
                lock (sub) sub.Gate.RequestKeyframe();
        }

        /// <summary>Returns true when the publisher should be asked for a keyframe on this member's behalf.</summary>
        public bool OfferVideo(uint streamId, byte[] frame, bool keyframe)
        {
            if (!_subscriptions.TryGetValue(streamId, out var sub))
                return false;
            GateDecision decision;
            lock (sub)
            {
                decision = sub.Gate.Offer(keyframe, Volatile.Read(ref sub.Pending));
            }
            switch (decision)
            {
                case GateDecision.Send:
                    Interlocked.Increment(ref sub.Pending);
                    _outbox.Writer.TryWrite((frame, streamId));
                    return false;
                case GateDecision.DropAndRequestKeyframe:
                    return true;
                default:
                    return false;
            }
        }

        public void Enqueue(byte[] frame, uint streamId) => _outbox.Writer.TryWrite((frame, streamId));

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
                        if (item.StreamId != 0 && _subscriptions.TryGetValue(item.StreamId, out var sub))
                            Interlocked.Decrement(ref sub.Pending);
                    }
                }
            }
            catch (Exception) { }
        }

        private sealed class Subscription
        {
            public Subscription(int maxPending)
            {
                Gate = new FrameGate(maxPending);
            }

            public FrameGate Gate { get; }
            public int Pending;
        }
    }
}
