using System.Security.Cryptography;
using System.Text;

namespace Beamcast.Net;

/// <summary>
/// Challenge/response so the room password never travels in clear text:
/// <c>proof = HMAC-SHA256(key = SHA256(password), message = nonce)</c>.
/// </summary>
public static class AuthProof
{
    public const int NonceBytes = 16;

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes));

    public static string Compute(string password, string nonce)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty));
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(nonce ?? string.Empty));
        return Convert.ToBase64String(mac);
    }

    public static bool Verify(string password, string nonce, string? proof)
    {
        if (string.IsNullOrEmpty(proof))
            return false;
        var expected = Compute(password, nonce);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(proof)
        );
    }
}
