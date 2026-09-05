using System.Buffers.Binary;

namespace Beamcast.Net;

/// <summary>
/// What the app and a Beamcast server say to each other. Shared by both projects.
///
/// One WebSocket per member. The conversation opens with JSON text frames (create or join a
/// lounge, prove the password) and then switches to binary frames laid out by
/// <see cref="LoungeMux"/>. The server never learns the lounge password or the content key:
/// it stores a verifier derived from the key, checks HMAC proofs against it, and shuffles
/// opaque encrypted blobs (presence, stream metadata, media) between members.
/// </summary>
public static class LoungeProtocol
{
    public const int Version = 2;
    public const string DefaultPath = "/ws";
    public const int DefaultPort = 47710;

    public const string OpCreate = "create";
    public const string OpJoin = "join";

    public const string ReasonBadRequest = "bad_request";
    public const string ReasonVersion = "version";
    public const string ReasonBadKey = "app_key";
    public const string ReasonNoLounge = "no_lounge";
    public const string ReasonBadPassword = "bad_password";

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 6;
    public const int MaxNameLength = 48;

    public static string NewCode()
    {
        var chars = new char[CodeLength];
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(CodeLength);
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    public static string NormalizeCode(string? code) =>
        new((code ?? string.Empty).Trim().ToUpperInvariant().Where(CodeAlphabet.Contains).ToArray());

    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length == CodeLength && code.All(CodeAlphabet.Contains);

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
}

/// <summary>First text frame from the client.</summary>
public sealed class LoungeRequest
{
    public int Version { get; set; } = LoungeProtocol.Version;
    public string Op { get; set; } = LoungeProtocol.OpJoin;
    public string? AppKey { get; set; }

    /// <summary>Create: the lounge's display name.</summary>
    public string? Name { get; set; }

    /// <summary>Create: random salt (base64) the password is stretched with.</summary>
    public string? Salt { get; set; }

    /// <summary>Create: HMAC verifier derived from the stretched key (base64). Never the key itself.</summary>
    public string? Verifier { get; set; }

    /// <summary>Join: the lounge code.</summary>
    public string? Code { get; set; }
}

/// <summary>Server's answer to a join request, before the password proof.</summary>
public sealed class LoungeChallenge
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Salt { get; set; }
    public string? Nonce { get; set; }
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
}

public sealed class LoungeStreamInfo
{
    public uint Id { get; set; }
    public uint Owner { get; set; }
    public string? Meta { get; set; }
}

/// <summary>Server's welcome: who is here and what is being streamed (all blobs opaque to the server).</summary>
public sealed class LoungeWelcome
{
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public uint MemberId { get; set; }
    public List<LoungeMemberInfo> Members { get; set; } = [];
    public List<LoungeStreamInfo> Streams { get; set; } = [];
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

    public const byte MemberJoined = 9;
    public const byte MemberLeft = 10;

    /// <summary>Server→clients: a=streamId, b=ownerId, payload=meta blob.</summary>
    public const byte StreamStarted = 11;

    public const byte StreamEnded = 12;

    /// <summary>Server→publisher: a=streamId, b=the client's request tag.</summary>
    public const byte PublishAck = 13;

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
