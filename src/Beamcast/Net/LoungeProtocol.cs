using System.Buffers.Binary;

namespace Beamcast.Net;

/// <summary>
/// What the app and a Beamcast server (a "host") say to each other. Shared by both projects.
///
/// A host has many rooms. One WebSocket per member. The conversation opens with JSON text frames
/// (create or join a room, prove the password) and then switches to binary frames laid out by
/// <see cref="LoungeMux"/>. The server never learns a room password or its content key: for
/// password rooms it stores a verifier derived from the key and checks HMAC proofs against it;
/// for rooms without a password the key is handed from a member already inside to the newcomer
/// through an ECDH-wrapped blob the server merely forwards. Everything members exchange
/// (presence, stream metadata, media) is an opaque encrypted blob to the server.
/// </summary>
public static class LoungeProtocol
{
    public const int Version = 3;
    public const string DefaultPath = "/ws";
    public const string RoomsPath = "/rooms";
    public const string InfoPath = "/info";
    public const string AppKeyHeader = "X-Beamcast-Key";

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>A peer that sends nothing for this long is treated as gone.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a newcomer waits for a member to hand over the room key.</summary>
    public static readonly TimeSpan KeyHandoffTimeout = TimeSpan.FromSeconds(15);

    public const int DefaultPort = 47710;

    public const string OpCreate = "create";
    public const string OpJoin = "join";

    public const string ReasonBadRequest = "bad_request";
    public const string ReasonVersion = "version";
    public const string ReasonBadKey = "app_key";
    public const string ReasonNoLounge = "no_lounge";
    public const string ReasonBadPassword = "bad_password";
    public const string ReasonRoomFull = "room_full";
    public const string ReasonInviteExpired = "invite_expired";
    public const string ReasonRateLimited = "rate_limited";
    public const string ReasonNotOwner = "not_owner";
    public const string ReasonNotAllowed = "not_allowed";
    public const string ReasonKicked = "kicked";
    public const string ReasonRoomDeleted = "room_deleted";
    public const string ReasonPasswordChanged = "password_changed";
    public const string ReasonNoKey = "no_key";

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int PublicCodeLength = 6;
    public const int PrivateCodeLength = 10;
    public const int MinCodeLength = 6;
    public const int MaxCodeLength = 12;
    public const int MaxNameLength = 48;
    public const int MaxMembersCap = 100;
    public const double MinTtlHours = 0.01;
    public const double MaxTtlHours = 24 * 30;
    public const double DefaultTtlHours = 24;

    public static string NewCode(int length)
    {
        length = Math.Clamp(length, MinCodeLength, MaxCodeLength);
        var chars = new char[length];
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    public static string NormalizeCode(string? code) =>
        new((code ?? string.Empty).Trim().ToUpperInvariant().Where(CodeAlphabet.Contains).ToArray());

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length >= MinCodeLength && code.Length <= MaxCodeLength && code.All(CodeAlphabet.Contains);

    public static double ClampTtlHours(double hours) =>
        double.IsFinite(hours) && hours > 0 ? Math.Clamp(hours, MinTtlHours, MaxTtlHours) : DefaultTtlHours;

    public static int ClampMaxMembers(int value) => value <= 0 ? 0 : Math.Clamp(value, 2, MaxMembersCap);

    /// <summary>Accepts host, host:port, ws://…, wss://… and returns a full WebSocket URL.</summary>
    public static bool TryNormalizeServer(string? input, out string url)
    {
        url = string.Empty;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            text = "ws://" + text[7..];
        else if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "wss://" + text[8..];
        else if (!text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            text = "ws://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return false;

        var builder = new UriBuilder(uri);
        if (uri.IsDefaultPort && uri.Scheme == "ws" && !text.Contains(":" + uri.Port))
            builder.Port = DefaultPort;
        if (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
            builder.Path = DefaultPath;
        url = builder.Uri.ToString();
        return true;
    }

    /// <summary>The HTTP URL of a sibling endpoint (rooms list, info) for a WebSocket server URL.</summary>
    public static string HttpUrl(string serverUrl, string path)
    {
        var uri = new Uri(serverUrl);
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "wss" ? "https" : "http",
            Path = uri.AbsolutePath.EndsWith(DefaultPath, StringComparison.Ordinal)
                ? uri.AbsolutePath[..^DefaultPath.Length] + path
                : path,
        };
        return builder.Uri.ToString();
    }

    /// <summary>Short label for a server URL: host[:port] without scheme or path.</summary>
    public static string DisplayHost(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            return serverUrl;
        var showPort = !(uri.Scheme == "ws" && uri.Port == DefaultPort) && !(uri.Scheme == "wss" && uri.Port == 443);
        return showPort ? $"{uri.Host}:{uri.Port}" : uri.Host;
    }
}

public static class RoomVisibility
{
    public const string Public = "public";
    public const string Private = "private";

    public static string Normalize(string? value) =>
        string.Equals(value, Public, StringComparison.OrdinalIgnoreCase) ? Public : Private;
}

public static class RoomKind
{
    public const string Permanent = "permanent";
    public const string Temporary = "temporary";

    public static string Normalize(string? value) =>
        string.Equals(value, Temporary, StringComparison.OrdinalIgnoreCase) ? Temporary : Permanent;
}

public static class BroadcastPolicy
{
    public const string Everyone = "everyone";
    public const string Owner = "owner";

    public static string Normalize(string? value) =>
        string.Equals(value, Owner, StringComparison.OrdinalIgnoreCase) ? Owner : Everyone;
}

/// <summary>What everyone may know about a room. Never carries secrets.</summary>
public sealed class RoomInfo
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Visibility { get; set; } = RoomVisibility.Private;
    public string Kind { get; set; } = RoomKind.Permanent;
    public double TtlHours { get; set; } = LoungeProtocol.DefaultTtlHours;
    public bool HasPassword { get; set; }
    public string Broadcast { get; set; } = BroadcastPolicy.Everyone;
    public int MaxMembers { get; set; }
    public int Members { get; set; }
    public int Streams { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsPublic => Visibility == RoomVisibility.Public;
    public bool IsTemporary => Kind == RoomKind.Temporary;
}

/// <summary>Answer of GET /info and GET /rooms.</summary>
public sealed class HostInfo
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int Protocol { get; set; }
    public bool RequiresAppKey { get; set; }
    public int PublicRooms { get; set; }
    public int MembersOnline { get; set; }
    public List<RoomInfo> Rooms { get; set; } = [];
}

/// <summary>First text frame from the client.</summary>
public sealed class LoungeRequest
{
    public int Version { get; set; } = LoungeProtocol.Version;
    public string Op { get; set; } = LoungeProtocol.OpJoin;
    public string? AppKey { get; set; }

    // ----- create -----
    public string? Name { get; set; }
    public string? Visibility { get; set; }
    public string? Kind { get; set; }
    public double TtlHours { get; set; }
    public string? Broadcast { get; set; }
    public int MaxMembers { get; set; }

    /// <summary>Create/update: random salt (base64) the password is stretched with. Absent = no password.</summary>
    public string? Salt { get; set; }

    /// <summary>Create/update: HMAC verifier derived from the stretched key (base64). Never the key itself.</summary>
    public string? Verifier { get; set; }

    // ----- join -----
    public string? Code { get; set; }

    /// <summary>Join: an invite token; a valid one skips the password.</summary>
    public string? Invite { get; set; }

    /// <summary>Join: the creator's token, so the server recognises the room owner.</summary>
    public string? OwnerToken { get; set; }

    /// <summary>Join: ephemeral ECDH public key (base64) a member can wrap the room key for.</summary>
    public string? JoinKey { get; set; }
}

/// <summary>Client's proof of the password: HMAC(verifier, nonce), base64.</summary>
public sealed class LoungeProof
{
    public string Proof { get; set; } = string.Empty;
}

public sealed class LoungeMemberInfo
{
    public uint Id { get; set; }
    public string? Presence { get; set; }
    public bool IsOwner { get; set; }
}

public sealed class LoungeStreamInfo
{
    public uint Id { get; set; }
    public uint Owner { get; set; }
    public string? Meta { get; set; }
}

/// <summary>
/// Every JSON reply the server sends during the handshake. <see cref="Stage"/> says which:
/// a challenge (password rooms: prove it) or the welcome (you are in). Errors carry a reason.
/// </summary>
public sealed class LoungeReply
{
    public const string StageChallenge = "challenge";
    public const string StageWelcome = "welcome";

    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public string Stage { get; set; } = StageWelcome;

    // challenge
    public string? Salt { get; set; }
    public string? Nonce { get; set; }

    // welcome
    public uint MemberId { get; set; }
    public RoomInfo? Room { get; set; }
    public bool IsOwner { get; set; }

    /// <summary>Only right after creating a room. Keep it: it is the only way to manage the room.</summary>
    public string? OwnerToken { get; set; }

    /// <summary>True when the room key must come from a member already inside (no-password rooms).</summary>
    public bool NeedsKey { get; set; }

    public List<LoungeMemberInfo> Members { get; set; } = [];
    public List<LoungeStreamInfo> Streams { get; set; } = [];
}

/// <summary>Owner → server: fields left null are unchanged.</summary>
public sealed class RoomUpdateMessage
{
    public string? Name { get; set; }
    public string? Visibility { get; set; }
    public string? Kind { get; set; }
    public double? TtlHours { get; set; }
    public string? Broadcast { get; set; }
    public int? MaxMembers { get; set; }

    /// <summary>Set a (new) password: salt + verifier. Everyone else is asked to rejoin.</summary>
    public string? Salt { get; set; }
    public string? Verifier { get; set; }

    /// <summary>Remove the password. Members inside keep the key; newcomers get it from them.</summary>
    public bool ClearPassword { get; set; }
}

public sealed class InviteRequestMessage
{
    /// <summary>Seconds until the invite stops working; 0 = never.</summary>
    public long ExpiresInSeconds { get; set; }

    /// <summary>How many joins it allows; 0 = unlimited.</summary>
    public int MaxUses { get; set; }
}

public sealed class InviteCreatedMessage
{
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public int MaxUses { get; set; }
}

public sealed class ServerNotice
{
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Binary frame layout after the handshake: <c>[byte kind][uint32 a][uint32 b][payload]</c>.</summary>
public static class LoungeMux
{
    public const int HeaderSize = 9;

    /// <summary>Encrypted control message for everyone else. Client→server: a=b=0. Server→client: a=sender.</summary>
    public const byte Control = 1;

    /// <summary>Client→server: payload is the encrypted stream metadata; answered with <see cref="PublishAck"/>.</summary>
    public const byte Publish = 2;

    /// <summary>Client→server: a=streamId.</summary>
    public const byte Unpublish = 3;

    /// <summary>Both ways: a=streamId, payload is a framed Video/Audio message.</summary>
    public const byte Media = 4;

    public const byte Subscribe = 5;
    public const byte Unsubscribe = 6;

    /// <summary>Both ways: a=streamId. Server→publisher when a subscriber needs a keyframe.</summary>
    public const byte KeyframeRequest = 7;

    /// <summary>Client→server: payload is the member's encrypted presence blob. Server→clients: a=memberId.</summary>
    public const byte Presence = 8;

    /// <summary>Server→clients: a=memberId, b=1 when the newcomer is the room owner.</summary>
    public const byte MemberJoined = 9;
    public const byte MemberLeft = 10;

    /// <summary>Server→clients: a=streamId, b=ownerId, payload=meta blob.</summary>
    public const byte StreamStarted = 11;

    public const byte StreamEnded = 12;

    /// <summary>Server→publisher: a=streamId (0 = refused), b=the client's request tag.</summary>
    public const byte PublishAck = 13;

    /// <summary>
    /// Both ways, empty payload. Clients send one every <see cref="LoungeProtocol.HeartbeatInterval"/>
    /// and the server echoes it, so each side can tell a dead peer from a quiet one even through a
    /// proxy that keeps the origin socket open after the client vanished.
    /// </summary>
    public const byte Heartbeat = 14;

    /// <summary>Server→a member holding the key: a=newcomerId, payload=newcomer's public key.</summary>
    public const byte KeyRequest = 15;

    /// <summary>Member→server: a=newcomerId, payload=wrapped room key. Server→newcomer: a=sponsorId.</summary>
    public const byte KeyGrant = 16;

    /// <summary>Owner→server: JSON <see cref="RoomUpdateMessage"/>.</summary>
    public const byte RoomUpdate = 17;

    /// <summary>Server→clients: JSON <see cref="RoomInfo"/> after any change.</summary>
    public const byte RoomInfo = 18;

    /// <summary>Owner→server: a=tag, JSON <see cref="InviteRequestMessage"/>.</summary>
    public const byte InviteCreate = 19;

    /// <summary>Server→owner: b=tag, JSON <see cref="InviteCreatedMessage"/>.</summary>
    public const byte InviteCreated = 20;

    /// <summary>Owner→server: forget every invite.</summary>
    public const byte InviteRevokeAll = 21;

    /// <summary>Owner→server: a=memberId.</summary>
    public const byte Kick = 22;

    /// <summary>Owner→server: delete the room; everyone is sent away.</summary>
    public const byte RoomDelete = 23;

    /// <summary>Server→client: JSON <see cref="ServerNotice"/>; a=0, or the tag of the request it answers.</summary>
    public const byte Notice = 24;

    /// <summary>Server→client right before closing: JSON <see cref="ServerNotice"/> with the reason.</summary>
    public const byte Bye = 25;

    public static byte[] Encode(byte kind, uint a, uint b, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1), a);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(5), b);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out byte kind, out uint a, out uint b, out byte[] payload)
    {
        kind = 0;
        a = 0;
        b = 0;
        payload = [];
        if (frame.Length < HeaderSize)
            return false;
        kind = frame[0];
        a = BinaryPrimitives.ReadUInt32LittleEndian(frame[1..]);
        b = BinaryPrimitives.ReadUInt32LittleEndian(frame[5..]);
        payload = frame[HeaderSize..].ToArray();
        return true;
    }
}

/// <summary>Encrypted control messages members exchange through the server.</summary>
public sealed class PresenceMessage
{
    public string Name { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
}

public sealed class StreamMetaMessage
{
    public string Title { get; set; } = string.Empty;
    public string Codec { get; set; } = "h264";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public string? Audio { get; set; }
    public string State { get; set; } = StreamStates.Live;
}
