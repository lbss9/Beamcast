using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Beamcast.Net;

namespace Beamcast.Server;

/// <summary>
/// Rooms, their members and their streams. The server is deliberately blind: it checks password
/// proofs against a verifier, forwards opaque encrypted blobs (including the wrapped room key a
/// member hands to a newcomer), and routes media from a publisher to whoever subscribed, applying
/// a per-subscriber keyframe gate so a slow member never stalls anyone else. It cannot read names,
/// titles, keys or pictures.
/// </summary>
public sealed class LoungeHub
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LimiterWindow = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly JoinRateLimiter _perAddress = new(5, LimiterWindow);
    private readonly JoinRateLimiter _perRoom = new(30, LimiterWindow);
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
        rooms = _rooms.Count,
        members = _rooms.Values.Sum(l => l.MemberCount),
        streams = _rooms.Values.Sum(l => l.StreamCount),
        uptimeSeconds = Environment.TickCount64 / 1000,
    };

    public HostInfo HostInfo(bool includeRooms)
    {
        var publicRooms = _rooms.Values.Where(r => r.Visibility == RoomVisibility.Public).OrderByDescending(r => r.MemberCount).ThenBy(r => r.Name).ToList();
        return new HostInfo
        {
            Name = _options.HostName,
            Version = typeof(LoungeHub).Assembly.GetName().Version?.ToString(3) ?? "0",
            Protocol = LoungeProtocol.Version,
            RequiresAppKey = !string.IsNullOrEmpty(_options.AppKey),
            PublicRooms = publicRooms.Count,
            MembersOnline = _rooms.Values.Sum(r => r.MemberCount),
            Rooms = includeRooms ? publicRooms.Select(r => r.Info()).ToList() : [],
        };
    }

    public bool KeyMatches(string? presented)
    {
        if (string.IsNullOrEmpty(_options.AppKey))
            return true;
        var expected = Encoding.UTF8.GetBytes(_options.AppKey);
        var actual = Encoding.UTF8.GetBytes(presented ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void LoadPersisted()
    {
        foreach (var record in _store.Load())
        {
            try
            {
                var room = Room.FromRecord(record, this, _log);
                _rooms.TryAdd(room.Code, room);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping malformed room record {Code}.", record.Code);
            }
        }
        _log.LogInformation("{Count} room(s) restored.", _rooms.Count);
    }

    internal void Persist() => _store.Save(_rooms.Values.Select(r => r.ToRecord()));

    internal void Remove(Room room)
    {
        if (_rooms.TryRemove(room.Code, out _))
        {
            _log.LogInformation("Room {Code} deleted.", room.Code);
            Persist();
        }
    }

    /// <summary>Drops temporary rooms that have been empty longer than their TTL.</summary>
    public void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var room in _rooms.Values.Where(r => r.IsExpired(now)).ToList())
        {
            if (_rooms.TryRemove(room.Code, out _))
                removed++;
        }
        if (removed > 0)
        {
            _log.LogInformation("Swept {Count} expired temporary room(s).", removed);
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
            await RefuseAsync(socket, LoungeProtocol.ReasonBadRequest, ct);
            return;
        }
        if (request.Version != LoungeProtocol.Version)
        {
            await RefuseAsync(socket, LoungeProtocol.ReasonVersion, ct);
            return;
        }
        if (!KeyMatches(request.AppKey))
        {
            _log.LogWarning("Refused {Remote}: bad app key.", remote);
            await RefuseAsync(socket, LoungeProtocol.ReasonBadKey, ct);
            return;
        }

        Admission? admission;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            admission = request.Op == LoungeProtocol.OpCreate
                ? await CreateAsync(socket, request, remote, timeout.Token)
                : await JoinAsync(socket, request, remote, timeout.Token);
        }
        catch (Exception)
        {
            return;
        }

        if (admission is null)
            return;

        await admission.Room.RunMemberAsync(socket, remote, admission, ct);
    }

    private async Task<Admission?> CreateAsync(WebSocket socket, LoungeRequest request, string remote, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > LoungeProtocol.MaxNameLength || !TryReadPassword(request.Salt, request.Verifier, out var salt, out var verifier))
        {
            await RefuseAsync(socket, LoungeProtocol.ReasonBadRequest, ct);
            return null;
        }

        var visibility = RoomVisibility.Normalize(request.Visibility);
        var ownerToken = LoungeCrypto.NewToken();
        Room room;
        while (true)
        {
            var code = LoungeProtocol.NewCode(visibility == RoomVisibility.Public ? LoungeProtocol.PublicCodeLength : LoungeProtocol.PrivateCodeLength);
            room = new Room(code, name, this, _log)
            {
                Visibility = visibility,
                Kind = RoomKind.Normalize(request.Kind),
                TtlHours = request.TtlHours > 0 ? LoungeProtocol.ClampTtlHours(request.TtlHours) : _options.DefaultTemporaryTtlHours,
                Broadcast = BroadcastPolicy.Normalize(request.Broadcast),
                MaxMembers = LoungeProtocol.ClampMaxMembers(request.MaxMembers),
                Salt = salt,
                Verifier = verifier,
                OwnerTokenHash = LoungeCrypto.TokenHash(ownerToken),
            };
            if (_rooms.TryAdd(room.Code, room))
                break;
        }
        Persist();
        _log.LogInformation("Room {Code} \"{Name}\" ({Visibility}, {Kind}, password {Password}) created by {Remote}.",
            room.Code, name, room.Visibility, room.Kind, room.HasPassword ? "yes" : "no", remote);
        return new Admission(room, IsOwner: true, NeedsKey: false, JoinKey: null, OwnerToken: ownerToken);
    }

    private async Task<Admission?> JoinAsync(WebSocket socket, LoungeRequest request, string remote, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (_perAddress.IsBlocked(remote, now))
        {
            _log.LogWarning("Rate limited {Remote}.", remote);
            await RefuseAsync(socket, LoungeProtocol.ReasonRateLimited, ct);
            return null;
        }

        var code = LoungeProtocol.NormalizeCode(request.Code);
        if (!_rooms.TryGetValue(code, out var room))
        {
            _perAddress.RecordFailure(remote, now);
            await RefuseAsync(socket, LoungeProtocol.ReasonNoLounge, ct);
            return null;
        }
        if (_perRoom.IsBlocked(room.Code, now))
        {
            _log.LogWarning("Room {Code} rate limited (too many failed joins).", room.Code);
            await RefuseAsync(socket, LoungeProtocol.ReasonRateLimited, ct);
            return null;
        }

        var isOwner = LoungeCrypto.TokenMatches(room.OwnerTokenHash, request.OwnerToken);
        var admittedByInvite = false;
        if (!string.IsNullOrEmpty(request.Invite))
        {
            if (!room.TryConsumeInvite(request.Invite))
            {
                _perAddress.RecordFailure(remote, now);
                _perRoom.RecordFailure(room.Code, now);
                _log.LogWarning("Bad or expired invite for room {Code} from {Remote}.", room.Code, remote);
                await RefuseAsync(socket, LoungeProtocol.ReasonInviteExpired, ct);
                return null;
            }
            admittedByInvite = true;
        }

        if (room.HasPassword && !admittedByInvite && !isOwner)
        {
            var nonce = LoungeCrypto.NewNonce();
            await SendJsonAsync(socket, new LoungeReply
            {
                Ok = true,
                Stage = LoungeReply.StageChallenge,
                Salt = Convert.ToBase64String(room.Salt),
                Nonce = nonce,
            }, ct);

            var proof = await ReadJsonAsync<LoungeProof>(socket, ct);
            if (proof is null || !LoungeCrypto.VerifyProof(room.Verifier, nonce, proof.Proof))
            {
                _perAddress.RecordFailure(remote, now);
                _perRoom.RecordFailure(room.Code, now);
                _log.LogWarning("Wrong password for room {Code} from {Remote}.", room.Code, remote);
                await RefuseAsync(socket, LoungeProtocol.ReasonBadPassword, ct);
                return null;
            }
        }
        else if (room.HasPassword && isOwner)
        {
            // The owner token proves ownership, but the content key still comes from the password;
            // the owner's app derives it locally, so a challenge is still needed to hand it the salt.
            var nonce = LoungeCrypto.NewNonce();
            await SendJsonAsync(socket, new LoungeReply { Ok = true, Stage = LoungeReply.StageChallenge, Salt = Convert.ToBase64String(room.Salt), Nonce = nonce }, ct);
            var proof = await ReadJsonAsync<LoungeProof>(socket, ct);
            if (proof is null || !LoungeCrypto.VerifyProof(room.Verifier, nonce, proof.Proof))
            {
                _perAddress.RecordFailure(remote, now);
                await RefuseAsync(socket, LoungeProtocol.ReasonBadPassword, ct);
                return null;
            }
        }

        if (room.IsFull)
        {
            await RefuseAsync(socket, LoungeProtocol.ReasonRoomFull, ct);
            return null;
        }

        // Password rooms: the key comes from the password or the invite. Otherwise a member inside
        // hands it over, unless nobody is inside, in which case the newcomer mints a fresh key.
        var needsKey = !room.HasPassword && room.MemberCount > 0;
        byte[]? joinKey = null;
        if (needsKey)
        {
            if (!TryDecodeBase64(request.JoinKey, out joinKey) || joinKey.Length is < 30 or > 200)
            {
                await RefuseAsync(socket, LoungeProtocol.ReasonBadRequest, ct);
                return null;
            }
        }
        _perAddress.Clear(remote);
        return new Admission(room, isOwner, needsKey, joinKey, OwnerToken: null);
    }

    private static bool TryReadPassword(string? saltText, string? verifierText, out byte[] salt, out byte[] verifier)
    {
        salt = [];
        verifier = [];
        if (string.IsNullOrEmpty(saltText) && string.IsNullOrEmpty(verifierText))
            return true;
        if (!TryDecodeBase64(saltText, out salt) || !TryDecodeBase64(verifierText, out verifier))
            return false;
        return salt.Length == LoungeCrypto.SaltBytes && verifier.Length == 32;
    }

    private static bool TryDecodeBase64(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            bytes = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Task RefuseAsync(WebSocket socket, string reason, CancellationToken ct) =>
        SendJsonAsync(socket, new LoungeReply { Ok = false, Reason = reason }, ct);

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

    internal sealed record Admission(Room Room, bool IsOwner, bool NeedsKey, byte[]? JoinKey, string? OwnerToken);
}

/// <summary>Periodically drops temporary rooms nobody has used for their TTL.</summary>
public sealed class LoungeJanitor : BackgroundService
{
    private readonly LoungeHub _hub;

    public LoungeJanitor(LoungeHub hub)
    {
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            _hub.Sweep();
    }
}

/// <summary>One room: settings, members, streams, invites, and the routing between members.</summary>
internal sealed class Room
{
    private const int MaxPendingFramesPerSubscriber = 4;
    private const int MaxInvites = 50;
    private static readonly TimeSpan SponsorTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<uint, Member> _members = new();
    private readonly ConcurrentDictionary<uint, RoomStream> _streams = new();
    private readonly List<InviteRecord> _invites = [];
    private readonly LoungeHub _hub;
    private readonly ILogger _log;
    private uint _nextMemberId;
    private uint _nextStreamId;

    public Room(string code, string name, LoungeHub hub, ILogger log)
    {
        Code = code;
        Name = name;
        _hub = hub;
        _log = log;
        CreatedAt = DateTimeOffset.UtcNow;
        LastActiveAt = CreatedAt;
    }

    public string Code { get; }
    public string Name { get; set; }
    public string Visibility { get; set; } = RoomVisibility.Private;
    public string Kind { get; set; } = RoomKind.Permanent;
    public double TtlHours { get; set; } = LoungeProtocol.DefaultTtlHours;
    public string Broadcast { get; set; } = BroadcastPolicy.Everyone;
    public int MaxMembers { get; set; }
    public byte[] Salt { get; set; } = [];
    public byte[] Verifier { get; set; } = [];
    public string OwnerTokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastActiveAt { get; set; }

    public bool HasPassword => Verifier.Length > 0;
    public int MemberCount => _members.Count;
    public int StreamCount => _streams.Count;
    public bool IsFull => MaxMembers > 0 && _members.Count >= MaxMembers;

    public bool IsExpired(DateTimeOffset now) =>
        Kind == RoomKind.Temporary && _members.IsEmpty && LastActiveAt + TimeSpan.FromHours(TtlHours) < now;

    public static Room FromRecord(RoomRecord record, LoungeHub hub, ILogger log)
    {
        var room = new Room(record.Code, record.Name, hub, log)
        {
            CreatedAt = record.CreatedAt,
            LastActiveAt = record.LastActiveAt,
            Visibility = RoomVisibility.Normalize(record.Visibility),
            Kind = RoomKind.Normalize(record.Kind),
            TtlHours = LoungeProtocol.ClampTtlHours(record.TtlHours),
            Broadcast = BroadcastPolicy.Normalize(record.Broadcast),
            MaxMembers = LoungeProtocol.ClampMaxMembers(record.MaxMembers),
            Salt = string.IsNullOrEmpty(record.Salt) ? [] : Convert.FromBase64String(record.Salt),
            Verifier = string.IsNullOrEmpty(record.Verifier) ? [] : Convert.FromBase64String(record.Verifier),
            OwnerTokenHash = record.OwnerTokenHash ?? string.Empty,
        };
        if (!LoungeProtocol.IsValidCode(room.Code))
            throw new FormatException("bad code");
        lock (room._invites)
            room._invites.AddRange(record.Invites ?? []);
        return room;
    }

    public RoomRecord ToRecord()
    {
        lock (_invites)
        {
            return new RoomRecord
            {
                Code = Code,
                Name = Name,
                Visibility = Visibility,
                Kind = Kind,
                TtlHours = TtlHours,
                Broadcast = Broadcast,
                MaxMembers = MaxMembers,
                Salt = Salt.Length == 0 ? string.Empty : Convert.ToBase64String(Salt),
                Verifier = Verifier.Length == 0 ? string.Empty : Convert.ToBase64String(Verifier),
                OwnerTokenHash = OwnerTokenHash,
                Invites = _invites.ToList(),
                CreatedAt = CreatedAt,
                LastActiveAt = LastActiveAt,
            };
        }
    }

    public RoomInfo Info() => new()
    {
        Code = Code,
        Name = Name,
        Visibility = Visibility,
        Kind = Kind,
        TtlHours = TtlHours,
        HasPassword = HasPassword,
        Broadcast = Broadcast,
        MaxMembers = MaxMembers,
        Members = _members.Count,
        Streams = _streams.Count,
        CreatedAt = CreatedAt,
    };

    public bool TryConsumeInvite(string token)
    {
        var hash = LoungeCrypto.TokenHash(token);
        var now = DateTimeOffset.UtcNow;
        lock (_invites)
        {
            _invites.RemoveAll(i => !i.IsUsable(now));
            var invite = _invites.FirstOrDefault(i => i.TokenHash == hash);
            if (invite is null)
                return false;
            invite.Uses++;
        }
        _hub.Persist();
        return true;
    }

    public async Task RunMemberAsync(WebSocket socket, string remote, LoungeHub.Admission admission, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var member = new Member(Interlocked.Increment(ref _nextMemberId), socket, admission.IsOwner, linked)
        {
            KeyPending = admission.NeedsKey,
            JoinKey = admission.JoinKey,
        };
        _members[member.Id] = member;
        LastActiveAt = DateTimeOffset.UtcNow;

        var welcome = new LoungeReply
        {
            Ok = true,
            Stage = LoungeReply.StageWelcome,
            MemberId = member.Id,
            Room = Info(),
            IsOwner = member.IsOwner,
            OwnerToken = admission.OwnerToken,
            NeedsKey = admission.NeedsKey,
            Members = _members.Values.Where(m => m.Id != member.Id)
                .Select(m => new LoungeMemberInfo { Id = m.Id, IsOwner = m.IsOwner, Presence = m.Presence is null ? null : Convert.ToBase64String(m.Presence) })
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

        _log.LogInformation("Member {Id} from {Remote} entered room {Code} ({Count} online{Owner}).", member.Id, remote, Code, _members.Count, member.IsOwner ? ", owner" : "");
        SendAll(LoungeMux.Encode(LoungeMux.MemberJoined, member.Id, member.IsOwner ? 1u : 0u, ReadOnlySpan<byte>.Empty), except: member.Id);
        if (member.KeyPending)
            _ = Task.Run(() => KeyHandoffAsync(member));

        member.Sender = member.SendLoopAsync(linked.Token);
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
                        _log.LogInformation("Member {Id} in room {Code} timed out.", member.Id, Code);
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
            await SafeAwait(member.Sender);
            await SafeCloseAsync(socket);
            _log.LogInformation("Member {Id} left room {Code} ({Count} online).", member.Id, Code, _members.Count);
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
                SendAll(LoungeMux.Encode(LoungeMux.Presence, member.Id, 0, payload), except: member.Id);
                break;

            case LoungeMux.Control:
                SendAll(LoungeMux.Encode(LoungeMux.Control, member.Id, 0, payload), except: member.Id);
                break;

            case LoungeMux.KeyGrant:
                if (_members.TryGetValue(a, out var newcomer) && newcomer.KeyPending && !member.KeyPending)
                {
                    newcomer.KeyPending = false;
                    newcomer.Enqueue(LoungeMux.Encode(LoungeMux.KeyGrant, member.Id, 0, payload), 0);
                }
                break;

            case LoungeMux.Publish:
            {
                if (Broadcast == BroadcastPolicy.Owner && !member.IsOwner)
                {
                    member.Enqueue(LoungeMux.Encode(LoungeMux.PublishAck, 0, a, ReadOnlySpan<byte>.Empty), 0);
                    break;
                }
                var stream = new RoomStream(Interlocked.Increment(ref _nextStreamId), member.Id, payload);
                _streams[stream.Id] = stream;
                member.Enqueue(LoungeMux.Encode(LoungeMux.PublishAck, stream.Id, a, ReadOnlySpan<byte>.Empty), 0);
                SendAll(LoungeMux.Encode(LoungeMux.StreamStarted, stream.Id, member.Id, payload), except: member.Id);
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

            case LoungeMux.RoomUpdate:
            case LoungeMux.InviteCreate:
            case LoungeMux.InviteRevokeAll:
            case LoungeMux.Kick:
            case LoungeMux.RoomDelete:
                if (!member.IsOwner)
                {
                    Notify(member, LoungeProtocol.ReasonNotOwner, a);
                    break;
                }
                HandleOwner(member, kind, a, payload);
                break;
        }
    }

    private void HandleOwner(Member owner, byte kind, uint tag, byte[] payload)
    {
        switch (kind)
        {
            case LoungeMux.RoomUpdate:
                if (Json.Deserialize<RoomUpdateMessage>(payload) is { } update)
                    ApplyUpdate(owner, update);
                break;

            case LoungeMux.InviteCreate:
            {
                var request = Json.Deserialize<InviteRequestMessage>(payload) ?? new InviteRequestMessage();
                var token = LoungeCrypto.NewToken(18);
                var record = new InviteRecord
                {
                    TokenHash = LoungeCrypto.TokenHash(token),
                    ExpiresAt = request.ExpiresInSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(request.ExpiresInSeconds, 365L * 86400)) : null,
                    MaxUses = Math.Clamp(request.MaxUses, 0, 1000),
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                lock (_invites)
                {
                    _invites.RemoveAll(i => !i.IsUsable(DateTimeOffset.UtcNow));
                    if (_invites.Count >= MaxInvites)
                        _invites.RemoveAt(0);
                    _invites.Add(record);
                }
                _hub.Persist();
                var created = new InviteCreatedMessage { Token = token, ExpiresAt = record.ExpiresAt, MaxUses = record.MaxUses };
                owner.Enqueue(LoungeMux.Encode(LoungeMux.InviteCreated, 0, tag, Json.Serialize(created)), 0);
                _log.LogInformation("Invite created for room {Code} (expires {Expires}, uses {Uses}).", Code, record.ExpiresAt?.ToString("u") ?? "never", record.MaxUses == 0 ? "unlimited" : record.MaxUses.ToString());
                break;
            }

            case LoungeMux.InviteRevokeAll:
                lock (_invites)
                    _invites.Clear();
                _hub.Persist();
                Notify(owner, "invites_revoked", tag);
                break;

            case LoungeMux.Kick:
                if (_members.TryGetValue(tag, out var victim) && !victim.IsOwner)
                {
                    _log.LogInformation("Member {Id} kicked from room {Code}.", victim.Id, Code);
                    victim.CloseWith(LoungeProtocol.ReasonKicked);
                }
                break;

            case LoungeMux.RoomDelete:
                _hub.Remove(this);
                foreach (var member in _members.Values)
                    member.CloseWith(LoungeProtocol.ReasonRoomDeleted);
                break;
        }
    }

    private void ApplyUpdate(Member owner, RoomUpdateMessage update)
    {
        var passwordChanged = false;
        if (update.Name is { } name && name.Trim().Length is > 0 and <= LoungeProtocol.MaxNameLength)
            Name = name.Trim();
        if (update.Visibility is not null)
            Visibility = RoomVisibility.Normalize(update.Visibility);
        if (update.Kind is not null)
            Kind = RoomKind.Normalize(update.Kind);
        if (update.TtlHours is { } ttl)
            TtlHours = LoungeProtocol.ClampTtlHours(ttl);
        if (update.Broadcast is not null)
            Broadcast = BroadcastPolicy.Normalize(update.Broadcast);
        if (update.MaxMembers is { } max)
            MaxMembers = LoungeProtocol.ClampMaxMembers(max);
        if (update.ClearPassword)
        {
            Salt = [];
            Verifier = [];
        }
        else if (!string.IsNullOrEmpty(update.Salt) && !string.IsNullOrEmpty(update.Verifier))
        {
            try
            {
                var salt = Convert.FromBase64String(update.Salt);
                var verifier = Convert.FromBase64String(update.Verifier);
                if (salt.Length == LoungeCrypto.SaltBytes && verifier.Length == 32)
                {
                    Salt = salt;
                    Verifier = verifier;
                    passwordChanged = true;
                }
            }
            catch (FormatException) { }
        }

        if (Broadcast == BroadcastPolicy.Owner)
        {
            foreach (var stream in _streams.Values.Where(s => s.Owner != owner.Id).ToList())
                EndStream(stream);
        }

        _hub.Persist();
        SendAll(LoungeMux.Encode(LoungeMux.RoomInfo, 0, 0, Json.Serialize(Info())), except: null);
        _log.LogInformation("Room {Code} updated by its owner.", Code);

        if (passwordChanged)
        {
            // Everyone else holds a key derived from the old password; they must come back with the new one.
            foreach (var member in _members.Values.Where(m => m.Id != owner.Id).ToList())
                member.CloseWith(LoungeProtocol.ReasonPasswordChanged);
        }
    }

    private void Notify(Member member, string reason, uint tag) =>
        member.Enqueue(LoungeMux.Encode(LoungeMux.Notice, tag, 0, Json.Serialize(new ServerNotice { Reason = reason })), 0);

    /// <summary>Asks members holding the key, one at a time, to wrap it for the newcomer.</summary>
    private async Task KeyHandoffAsync(Member newcomer)
    {
        var tried = new HashSet<uint>();
        var deadline = DateTimeOffset.UtcNow + LoungeProtocol.KeyHandoffTimeout;
        while (newcomer.KeyPending && _members.ContainsKey(newcomer.Id) && DateTimeOffset.UtcNow < deadline)
        {
            var sponsor = _members.Values
                .Where(m => m.Id != newcomer.Id && !m.KeyPending && !tried.Contains(m.Id))
                .OrderBy(m => m.Id)
                .FirstOrDefault();
            if (sponsor is null)
            {
                if (!_members.Values.Any(m => m.Id != newcomer.Id && !m.KeyPending))
                {
                    // Everyone who held the key is gone: the newcomer starts a fresh key.
                    newcomer.KeyPending = false;
                    newcomer.Enqueue(LoungeMux.Encode(LoungeMux.KeyGrant, 0, 0, ReadOnlySpan<byte>.Empty), 0);
                    return;
                }
                tried.Clear();
                await Task.Delay(500);
                continue;
            }
            tried.Add(sponsor.Id);
            sponsor.Enqueue(LoungeMux.Encode(LoungeMux.KeyRequest, newcomer.Id, 0, newcomer.JoinKey ?? []), 0);
            var waitUntil = DateTimeOffset.UtcNow + SponsorTimeout;
            while (newcomer.KeyPending && DateTimeOffset.UtcNow < waitUntil)
                await Task.Delay(100);
        }
        if (newcomer.KeyPending && _members.ContainsKey(newcomer.Id))
        {
            _log.LogWarning("No member handed the key to {Id} in room {Code}.", newcomer.Id, Code);
            newcomer.CloseWith(LoungeProtocol.ReasonNoKey);
        }
    }

    private void RouteMedia(RoomStream stream, byte[] framed)
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

    private void RequestKeyframe(RoomStream stream)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref stream.LastKeyframeRequestTicks);
        if (now - last < 250)
            return;
        Interlocked.Exchange(ref stream.LastKeyframeRequestTicks, now);
        if (_members.TryGetValue(stream.Owner, out var owner))
            owner.Enqueue(LoungeMux.Encode(LoungeMux.KeyframeRequest, stream.Id, 0, ReadOnlySpan<byte>.Empty), 0);
    }

    private void EndStream(RoomStream stream)
    {
        if (!_streams.TryRemove(stream.Id, out _))
            return;
        foreach (var subscriberId in stream.Subscribers.Keys)
        {
            if (_members.TryGetValue(subscriberId, out var subscriber))
                subscriber.Unsubscribe(stream.Id);
        }
        SendAll(LoungeMux.Encode(LoungeMux.StreamEnded, stream.Id, stream.Owner, ReadOnlySpan<byte>.Empty), except: null);
    }

    private void Leave(Member member)
    {
        if (!_members.TryRemove(member.Id, out _))
            return;
        foreach (var stream in _streams.Values.Where(s => s.Owner == member.Id).ToList())
            EndStream(stream);
        foreach (var stream in _streams.Values)
            stream.Subscribers.TryRemove(member.Id, out _);
        SendAll(LoungeMux.Encode(LoungeMux.MemberLeft, member.Id, 0, ReadOnlySpan<byte>.Empty), except: null);
        LastActiveAt = DateTimeOffset.UtcNow;
    }

    private void SendAll(byte[] frame, uint? except)
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

    private static async Task SafeAwait(Task? task)
    {
        if (task is null)
            return;
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
    private sealed class RoomStream
    {
        public RoomStream(uint id, uint owner, byte[] meta)
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
        private readonly CancellationTokenSource _lifetime;
        private int _closing;

        public Member(uint id, WebSocket socket, bool isOwner, CancellationTokenSource lifetime)
        {
            Id = id;
            Socket = socket;
            IsOwner = isOwner;
            _lifetime = lifetime;
        }

        public uint Id { get; }
        public WebSocket Socket { get; }
        public bool IsOwner { get; }
        public byte[]? Presence { get; set; }
        public volatile bool KeyPending;
        public byte[]? JoinKey { get; set; }
        public Task? Sender { get; set; }

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

        /// <summary>Tells the member why and then closes: the reason is flushed before the socket goes.</summary>
        public void CloseWith(string reason)
        {
            if (Interlocked.Exchange(ref _closing, 1) != 0)
                return;
            Enqueue(LoungeMux.Encode(LoungeMux.Bye, 0, 0, Json.Serialize(new ServerNotice { Reason = reason })), 0);
            Complete();
            _ = Task.Run(async () =>
            {
                await SafeAwait(Sender);
                try
                {
                    _lifetime.Cancel();
                }
                catch (ObjectDisposedException) { }
            });
        }

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
