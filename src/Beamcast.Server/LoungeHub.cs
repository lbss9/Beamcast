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
        // Since 2.4.0 every room is listed; a lock (password) is what keeps people out, not secrecy.
        // Visibility is kept in the records and the protocol for older clients but no longer hides anything.
        var publicRooms = _rooms.Values.OrderByDescending(r => r.MemberCount).ThenBy(r => r.Name).ToList();
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
        return new Admission(room, IsOwner: true, NeedsKey: false, JoinKey: null, OwnerToken: ownerToken, ClientIdOf(request));
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

        // Rooms migrated from 2.0 have no owner, so nobody could ever edit or delete them. The
        // first person to get in (past the password, when there is one) becomes the owner and
        // receives the token, exactly as if they had just created the room.
        string? claimedToken = null;
        if (room.OwnerTokenHash.Length == 0)
        {
            claimedToken = LoungeCrypto.NewToken();
            room.OwnerTokenHash = LoungeCrypto.TokenHash(claimedToken);
            isOwner = true;
            Persist();
            _log.LogInformation("Room {Code} had no owner; claimed by {Remote}.", room.Code, remote);
        }
        return new Admission(room, isOwner, needsKey, joinKey, OwnerToken: claimedToken, ClientIdOf(request));
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

    internal sealed record Admission(Room Room, bool IsOwner, bool NeedsKey, byte[]? JoinKey, string? OwnerToken, string? ClientId = null);

    /// <summary>The client's installation id, when it sent a sane one (opaque, ASCII letters and digits, at most 64).</summary>
    private static string? ClientIdOf(LoungeRequest request) =>
        request.Client is { Length: > 0 and <= 64 } id && id.All(char.IsAsciiLetterOrDigit) ? id : null;
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
