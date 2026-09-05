using System.Security.Cryptography;
using System.Text;

namespace Beamcast.Net;

/// <summary>
/// How a lounge password turns into keys, and what the server is allowed to know.
///
/// <code>
/// key       = PBKDF2-SHA256(password, salt, 200 000 rounds)   never leaves the member's machine
/// verifier  = HMAC(key, "verify")                              stored by the server at creation
/// proof     = HMAC(verifier, nonce)                            what a joiner sends; server can check it
/// content   = HKDF(key, "content")                             AES-256-GCM key for everything else
/// </code>
///
/// The server can confirm someone knows the password without being able to derive the content
/// key from anything it stores or sees. A malicious server could still brute-force a weak password
/// offline against the verifier; the 200k-round stretch makes that cost about 100 ms per guess.
/// </summary>
public static class LoungeCrypto
{
    public const int SaltBytes = 16;
    public const int KeyBytes = 32;
    public const int Iterations = 200_000;

    private static readonly byte[] VerifyInfo = Encoding.UTF8.GetBytes("beamcast/lounge/verify/v2");
    private static readonly byte[] ContentInfo = Encoding.UTF8.GetBytes("beamcast/lounge/content/v2");

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    public static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? string.Empty), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);

    public static byte[] Verifier(byte[] key) => HMACSHA256.HashData(key, VerifyInfo);

    public static byte[] ContentKey(byte[] key) => HKDF.DeriveKey(HashAlgorithmName.SHA256, key, KeyBytes, salt: null, info: ContentInfo);

    public static string Proof(byte[] verifier, string nonce) =>
        Convert.ToBase64String(HMACSHA256.HashData(verifier, Encoding.UTF8.GetBytes(nonce ?? string.Empty)));

    public static bool VerifyProof(byte[] verifier, string nonce, string? proof)
    {
        if (string.IsNullOrEmpty(proof))
            return false;
        byte[] presented;
        try
        {
            presented = Convert.FromBase64String(proof);
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = HMACSHA256.HashData(verifier, Encoding.UTF8.GetBytes(nonce ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
