using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Beamcast.Net;

namespace Beamcast.Server;

/// <summary>One room: settings, members, streams, invites, and the routing between members.</summary>
internal sealed partial class Room
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
            ClientId = admission.ClientId,
        };
        // A reconnect of a client whose old socket we have not noticed yet: drop the old member
        // now (ending its streams for everyone) so the room never lists the same person twice.
        if (member.ClientId is not null)
        {
            foreach (var stale in _members.Values.Where(m => m.ClientId == member.ClientId).ToList())
            {
                _log.LogInformation("Member {Old} in room {Code} replaced by a new connection of the same client.", stale.Id, Code);
                Leave(stale);
                stale.CloseWith(LoungeProtocol.ReasonReplaced);
            }
        }
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
                // Echo the client's send time and add ours: that is all a client needs for RTT and clock offset.
                member.Enqueue(LoungeMux.Encode(LoungeMux.Heartbeat, a, LoungeMux.ClockNow(), ReadOnlySpan<byte>.Empty), 0);
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
                    RouteMedia(stream2, payload, b);
                break;

            case LoungeMux.Subscribe:
                if (_streams.TryGetValue(a, out var target) && target.Owner != member.Id)
                {
                    member.Subscribe(target.Id, MaxPendingFramesPerSubscriber);
                    if (target.Subscribers.TryAdd(member.Id, 0))
                        NotifyViewer(target, member.Id, joined: true);
                    RequestKeyframe(target);
                }
                break;

            case LoungeMux.Unsubscribe:
                member.Unsubscribe(a);
                if (_streams.TryGetValue(a, out var left) && left.Subscribers.TryRemove(member.Id, out _))
                    NotifyViewer(left, member.Id, joined: false);
                break;

            case LoungeMux.KeyframeRequest:
                if (_streams.TryGetValue(a, out var wanted) && wanted.Subscribers.ContainsKey(member.Id))
                {
                    member.ResetGate(a);
                    RequestKeyframe(wanted);
                }
                break;

            case LoungeMux.ViewerReport:
                // A viewer says its delay is b ms: hand it to the publisher, stamped with who said so.
                if (_streams.TryGetValue(a, out var reported) && reported.Subscribers.ContainsKey(member.Id) && _members.TryGetValue(reported.Owner, out var publisher))
                {
                    Span<byte> who = stackalloc byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(who, member.Id);
                    publisher.Enqueue(LoungeMux.Encode(LoungeMux.ViewerReport, a, b, who), 0);
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

    /// <summary>Tells the publisher that someone started or stopped watching one of its streams.</summary>
    private void NotifyViewer(RoomStream stream, uint viewerId, bool joined)
    {
        if (_members.TryGetValue(stream.Owner, out var owner))
            owner.Enqueue(LoungeMux.Encode(joined ? LoungeMux.ViewerJoined : LoungeMux.ViewerLeft, stream.Id, viewerId, ReadOnlySpan<byte>.Empty), 0);
    }

    private void RouteMedia(RoomStream stream, byte[] framed, uint sendStamp)
    {
        Framing.TryPeek(framed, out var type, out var flags);
        var isVideo = type == MessageType.Video;
        var keyframe = (flags & MessageFlags.Keyframe) != 0;
        var needKeyframe = false;
        var outbound = LoungeMux.Encode(LoungeMux.Media, stream.Id, sendStamp, framed);

        foreach (var subscriberId in stream.Subscribers.Keys)
        {
            if (!_members.TryGetValue(subscriberId, out var subscriber))
            {
                if (stream.Subscribers.TryRemove(subscriberId, out _))
                    NotifyViewer(stream, subscriberId, joined: false);
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
        {
            if (stream.Subscribers.TryRemove(member.Id, out _))
                NotifyViewer(stream, member.Id, joined: false);
        }
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
}
