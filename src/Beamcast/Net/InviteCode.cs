using System.Net;
using System.Text;

namespace Beamcast.Net;

/// <summary>Where a viewer should connect to, plus the room password if the host set one.</summary>
public sealed record InviteTarget(string Host, int Port, string? Password)
{
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}

/// <summary>
/// A short shareable string that carries host, port and password. Encoded as
/// <c>BC-&lt;base64url("host|port|password")&gt;</c>. Decoding also accepts a bare
/// <c>host</c>, <c>host:port</c> or <c>[ipv6]:port</c> for people who prefer typing.
/// </summary>
public static class InviteCode
{
    public const string Prefix = "BC-";

    public static string Encode(InviteTarget target)
    {
        var host = (target.Host ?? string.Empty).Trim();
        var raw = $"{host}|{target.Port}|{target.Password ?? string.Empty}";
        return Prefix + Base64Url(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string? input, out InviteTarget target, int defaultPort = AppInfo.DefaultPort)
    {
        target = new InviteTarget(string.Empty, defaultPort, null);
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return TryDecodeCode(text[Prefix.Length..], out target);

        return TryParseAddress(text, defaultPort, out target);
    }

    private static bool TryDecodeCode(string body, out InviteTarget target)
    {
        target = new InviteTarget(string.Empty, AppInfo.DefaultPort, null);
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
        if (parts.Length < 2)
            return false;
        var host = parts[0].Trim();
        if (host.Length == 0 || !int.TryParse(parts[1], out var port) || !IsValidPort(port))
            return false;
        var password = parts.Length > 2 ? string.Join("|", parts.Skip(2)) : string.Empty;
        target = new InviteTarget(host, port, password.Length == 0 ? null : password);
        return true;
    }

    private static bool TryParseAddress(string text, int defaultPort, out InviteTarget target)
    {
        target = new InviteTarget(string.Empty, defaultPort, null);
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

        target = new InviteTarget(host, port, null);
        return true;
    }

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - s.Length % 4) % 4;
        return Convert.FromBase64String(s + new string('=', padding));
    }
}
