using System.Buffers.Binary;

namespace Beamcast.Net;

/// <summary>
/// What the app and the relay say to each other. Shared by both projects.
///
/// A client opens a WebSocket and sends one text frame (<see cref="RelayJoin"/>). The relay answers
/// with one text frame (<see cref="RelayJoinResult"/>). From then on everything is binary:
///
/// - A viewer's socket carries plain framed messages (<see cref="Framing"/>), exactly as the TCP
///   transport would, in both directions.
/// - The host's socket multiplexes all viewers: every binary frame starts with
///   <c>[uint32 viewerId][byte kind]</c> (<see cref="RelayMux"/>). Viewer id 0 means "every joined
///   viewer" and is only valid host → relay.
///
/// The relay never sees the room secret, so it cannot read the messages it forwards; it only reads
/// the type/flags prefix to apply the same per-viewer keyframe gate the direct host uses.
/// </summary>
public static class RelayProtocol
{
    public const int Version = 1;
    public const string DefaultPath = "/ws";

    public const string RoleHost = "host";
    public const string RoleViewer = "viewer";

    public const string ReasonBadKey = "app_key";
    public const string ReasonNoRoom = "no_room";
    public const string ReasonVersion = "version";
    public const string ReasonBadRequest = "bad_request";

    private const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int RoomCodeLength = 6;

    public static string NewRoomCode()
    {
        var chars = new char[RoomCodeLength];
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(RoomCodeLength);
        for (var i = 0; i < chars.Length; i++)
            chars[i] = RoomAlphabet[bytes[i] % RoomAlphabet.Length];
        return new string(chars);
    }

    public static string NormalizeRoomCode(string? code) =>
        new((code ?? string.Empty).Trim().ToUpperInvariant().Where(c => RoomAlphabet.Contains(c)).ToArray());

    public static bool IsValidRoomCode(string? code) =>
        !string.IsNullOrEmpty(code) && code.Length == RoomCodeLength && code.All(RoomAlphabet.Contains);
}

public sealed class RelayJoin
{
    public int Version { get; set; } = RelayProtocol.Version;
    public string Role { get; set; } = RelayProtocol.RoleViewer;
    public string? Room { get; set; }
    public string? AppKey { get; set; }
    public string? Name { get; set; }
}

public sealed class RelayJoinResult
{
    public bool Ok { get; set; }
    public string? Room { get; set; }
    public string? Reason { get; set; }
    public int Viewers { get; set; }
}

/// <summary>Framing of the host's multiplexed socket.</summary>
public static class RelayMux
{
    public const int HeaderSize = 5;
    public const uint Broadcast = 0;

    public const byte KindData = 0;
    public const byte KindJoined = 1;
    public const byte KindLeft = 2;

    public static byte[] Encode(uint viewerId, byte kind, ReadOnlySpan<byte> framed)
    {
        var buffer = new byte[HeaderSize + framed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, viewerId);
        buffer[4] = kind;
        framed.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out uint viewerId, out byte kind, out byte[] framed)
    {
        viewerId = 0;
        kind = 0;
        framed = [];
        if (frame.Length < HeaderSize)
            return false;
        viewerId = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        kind = frame[4];
        framed = frame[HeaderSize..].ToArray();
        return true;
    }
}
