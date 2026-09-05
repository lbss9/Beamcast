using System.Text;

namespace Beamcast.Net;

/// <summary>Server address plus lounge code. The password is never part of it.</summary>
public sealed record LoungeTarget(string ServerUrl, string Code);

/// <summary>
/// A shareable string: <c>BC-&lt;base64url("server|CODE")&gt;</c>. Decoding also accepts a bare
/// lounge code (uses the server from settings) or <c>server CODE</c> / <c>server/CODE</c>.
/// </summary>
public static class LoungeInvite
{
    public const string Prefix = "BC-";

    public static string Encode(LoungeTarget target) =>
        Prefix + Base64Url(Encoding.UTF8.GetBytes(target.ServerUrl + "|" + target.Code));

    public static bool TryDecode(string? input, string defaultServerUrl, out LoungeTarget target)
    {
        target = new LoungeTarget(string.Empty, string.Empty);
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try
            {
                bytes = FromBase64Url(text[Prefix.Length..]);
            }
            catch (FormatException)
            {
                return false;
            }
            var parts = Encoding.UTF8.GetString(bytes).Split('|');
            if (parts.Length != 2)
                return false;
            return Build(parts[0], parts[1], out target);
        }

        var code = LoungeProtocol.NormalizeCode(text);
        if (LoungeProtocol.IsValidCode(code) && text.Trim().Length <= LoungeProtocol.CodeLength + 1)
            return Build(defaultServerUrl, code, out target);

        var separators = new[] { ' ', '/', '#' };
        var index = text.LastIndexOfAny(separators);
        if (index > 0)
            return Build(text[..index], text[(index + 1)..], out target);

        return false;
    }

    private static bool Build(string server, string code, out LoungeTarget target)
    {
        target = new LoungeTarget(string.Empty, string.Empty);
        var normalized = LoungeProtocol.NormalizeCode(code);
        if (!LoungeProtocol.IsValidCode(normalized) || !LoungeProtocol.TryNormalizeServer(server, out var url))
            return false;
        target = new LoungeTarget(url, normalized);
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - s.Length % 4) % 4;
        return Convert.FromBase64String(s + new string('=', padding));
    }
}
