using System.Text;

namespace Beamcast.Net;

/// <summary>
/// Where an invite points: a host, a room, optionally an invite token (validated by the host, may
/// expire) and optionally the room's content key (password rooms, so the guest needs no password).
/// The password itself is never part of it.
/// </summary>
public sealed record LoungeTarget(string ServerUrl, string Code, string? InviteToken = null, byte[]? ContentKey = null);

/// <summary>
/// A shareable string: <c>BC-&lt;base64url("3|server|CODE|token|key")&gt;</c>, token and key may be
/// empty. Decoding also accepts the v2 form <c>server|CODE</c>, a bare room code (uses the given
/// default server) or <c>server CODE</c> / <c>server/CODE</c>.
/// </summary>
public static class LoungeInvite
{
    public const string Prefix = "BC-";
    private const string FormatVersion = "3";

    public static string Encode(LoungeTarget target)
    {
        var key = target.ContentKey is { Length: > 0 } ? Base64Url.Encode(target.ContentKey) : string.Empty;
        var text = string.Join('|', FormatVersion, target.ServerUrl, target.Code, target.InviteToken ?? string.Empty, key);
        return Prefix + Base64Url.Encode(Encoding.UTF8.GetBytes(text));
    }

    public static bool TryDecode(string? input, string defaultServerUrl, out LoungeTarget target)
    {
        target = new LoungeTarget(string.Empty, string.Empty);
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return false;

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!Base64Url.TryDecode(text[Prefix.Length..], out var bytes))
                return false;
            var parts = Encoding.UTF8.GetString(bytes).Split('|');
            if (parts.Length == 2)
                return Build(parts[0], parts[1], null, null, out target);
            if (parts.Length == 5 && parts[0] == FormatVersion)
            {
                byte[]? key = null;
                if (parts[4].Length > 0)
                {
                    if (!Base64Url.TryDecode(parts[4], out var decoded) || decoded.Length != LoungeCrypto.KeyBytes)
                        return false;
                    key = decoded;
                }
                return Build(parts[1], parts[2], parts[3].Length > 0 ? parts[3] : null, key, out target);
            }
            return false;
        }

        var code = LoungeProtocol.NormalizeCode(text);
        if (LoungeProtocol.IsValidCode(code) && text.Length <= LoungeProtocol.MaxCodeLength + 2)
            return Build(defaultServerUrl, code, null, null, out target);

        var separators = new[] { ' ', '/', '#' };
        var index = text.LastIndexOfAny(separators);
        if (index > 0)
            return Build(text[..index], text[(index + 1)..], null, null, out target);

        return false;
    }

    private static bool Build(string server, string code, string? token, byte[]? key, out LoungeTarget target)
    {
        target = new LoungeTarget(string.Empty, string.Empty);
        var normalized = LoungeProtocol.NormalizeCode(code);
        if (!LoungeProtocol.IsValidCode(normalized) || !LoungeProtocol.TryNormalizeServer(server, out var url))
            return false;
        target = new LoungeTarget(url, normalized, token, key);
        return true;
    }
}
