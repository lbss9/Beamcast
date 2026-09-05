using System.Net;
using System.Text;

namespace Beamcast.Net;

/// <summary>How a viewer reaches the host: straight to a TCP port, or through a relay room.</summary>
public enum InviteKind
{
    Direct,
    Relay,
}

/// <summary>
/// Where a viewer should connect to. <see cref="Secret"/> is the end-to-end encryption key
/// material and the proof of invitation; <see cref="Password"/> is an optional extra lock.
/// </summary>
public sealed record InviteTarget(
    InviteKind Kind,
    string Host,
    int Port,
    string? Password,
    string? Secret = null,
    string? RelayUrl = null,
    string? Room = null
)
{
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    public bool HasSecret => !string.IsNullOrEmpty(Secret);

    public static InviteTarget Direct(string host, int port, string? secret, string? password) =>
        new(InviteKind.Direct, host, port, password, secret);

    public static InviteTarget Relay(string relayUrl, string room, string? secret, string? password) =>
        new(InviteKind.Relay, string.Empty, 0, password, secret, relayUrl, room);
}

/// <summary>
/// A short shareable string that carries everything a viewer needs. Encoded as
/// <c>BC-&lt;base64url(fields joined by '|')&gt;</c>:
/// <list type="bullet">
/// <item><c>host|port|secret|password</c> for a direct connection (v1 codes were <c>host|port|password</c>)</item>
/// <item><c>relay|url|room|secret|password</c> for a relay room</item>
/// </list>
/// Decoding also accepts a bare <c>host</c>, <c>host:port</c> or <c>[ipv6]:port</c> for people who
/// prefer typing, but such targets have no secret and hosts refuse them unless legacy mode is on.
/// </summary>
public static class InviteCode
{
    public const string Prefix = "BC-";
    private const string RelayMarker = "relay";

    public static string Encode(InviteTarget target)
    {
        string raw;
        if (target.Kind == InviteKind.Relay)
        {
            raw = string.Join('|', RelayMarker, target.RelayUrl ?? string.Empty, target.Room ?? string.Empty, target.Secret ?? string.Empty, target.Password ?? string.Empty);
        }
        else
        {
            var host = (target.Host ?? string.Empty).Trim();
            raw = string.Join('|', host, target.Port.ToString(), target.Secret ?? string.Empty, target.Password ?? string.Empty);
        }
        return Prefix + Base64Url(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? input, out InviteTarget target, int defaultPort = AppInfo.DefaultPort)
    {
        target = InviteTarget.Direct(string.Empty, defaultPort, null, null);
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return TryDecodeCode(text[Prefix.Length..], out target);

        return TryParseAddress(text, defaultPort, out target);
    }

    private static bool TryDecodeCode(string body, out InviteTarget target)
    {
        target = InviteTarget.Direct(string.Empty, AppInfo.DefaultPort, null, null);
        byte[] bytes;
        try
        {
            bytes = FromBase64Url(body);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(bytes).Split('|');
        if (parts.Length >= 4 && string.Equals(parts[0], RelayMarker, StringComparison.OrdinalIgnoreCase))
        {
            var url = parts[1].Trim();
            var room = RelayProtocol.NormalizeRoomCode(parts[2]);
            var secret = parts[3].Trim();
            var password = parts.Length > 4 ? string.Join("|", parts.Skip(4)) : string.Empty;
            if (!IsValidRelayUrl(url) || !RelayProtocol.IsValidRoomCode(room) || secret.Length == 0)
                return false;
            target = InviteTarget.Relay(url, room, secret, password.Length == 0 ? null : password);
            return true;
        }

        if (parts.Length < 2)
            return false;
        var host = parts[0].Trim();
        if (host.Length == 0 || !int.TryParse(parts[1], out var port) || !IsValidPort(port))
            return false;
        // v1 codes were host|port|password; v2 codes are host|port|secret|password (password last so it may contain '|').
        var legacy = parts.Length == 3;
        var pass = legacy ? parts[2] : parts.Length > 3 ? string.Join("|", parts.Skip(3)) : string.Empty;
        var directSecret = legacy || parts.Length < 4 ? string.Empty : parts[2].Trim();
        target = InviteTarget.Direct(host, port, directSecret.Length == 0 ? null : directSecret, pass.Length == 0 ? null : pass);
        return true;
    }

    private static bool TryParseAddress(string text, int defaultPort, out InviteTarget target)
    {
        target = InviteTarget.Direct(string.Empty, defaultPort, null, null);
        string host;
        var port = defaultPort;

        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0)
                return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':') || !int.TryParse(rest[1..], out port))
                    return false;
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var split = text.Split(':');
            host = split[0];
            if (!int.TryParse(split[1], out port))
                return false;
        }
        else
        {
            host = text;
        }

        host = host.Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || !IsValidPort(port))
            return false;
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return false;

        target = InviteTarget.Direct(host, port, null, null);
        return true;
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    public static bool IsValidRelayUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == "wss" || uri.Scheme == "ws")
        && !string.IsNullOrEmpty(uri.Host);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - s.Length % 4) % 4;
        return Convert.FromBase64String(s + new string('=', padding));
    }
}
