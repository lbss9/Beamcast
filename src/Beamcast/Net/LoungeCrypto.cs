using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Beamcast.Net;

/// <summary>
/// How a room password turns into keys, how a key travels to a newcomer, and what the server is
/// allowed to know.
///
/// <code>
/// key       = PBKDF2-SHA256(password, salt, 200 000 rounds)   never leaves the member's machine
/// verifier  = HMAC(key, "verify")                              stored by the server at creation
/// proof     = HMAC(verifier, nonce)                            what a joiner sends; server can check it
/// content   = HKDF(key, "content")                             AES-256-GCM key for everything else
/// </code>
///
/// Rooms without a password use a random content key that only members hold. A newcomer sends an
/// ephemeral ECDH (P-256) public key; a member inside wraps the room key for it (ECDH → HKDF →
/// AES-256-GCM) and the server forwards the blob without being able to open it.
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
    public const int TokenBytes = 32;

    private static readonly byte[] VerifyInfo = Encoding.UTF8.GetBytes("beamcast/lounge/verify/v2");
    private static readonly byte[] ContentInfo = Encoding.UTF8.GetBytes("beamcast/lounge/content/v2");
    private static readonly byte[] GrantInfo = Encoding.UTF8.GetBytes("beamcast/room/keygrant/v3");

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    /// <summary>A fresh random content key for a room without a password.</summary>
    public static byte[] NewRoomKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    /// <summary>Owner and invite tokens: random, URL-safe, only their hash is stored server-side.</summary>
    public static string NewToken(int bytes = TokenBytes) => Base64Url.Encode(RandomNumberGenerator.GetBytes(bytes));

    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    public static bool TokenMatches(string? storedHash, string? presented)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(presented))
            return false;
        var expected = Encoding.UTF8.GetBytes(storedHash);
        var actual = Encoding.UTF8.GetBytes(TokenHash(presented));
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

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

    /// <summary>
    /// Wraps a room key for a newcomer's ephemeral public key. Output:
    /// <c>[u16 len][sponsor public key][12-byte nonce][ciphertext + tag]</c>.
    /// </summary>
    public static byte[] WrapRoomKey(byte[] roomKey, byte[] newcomerPublicKey)
    {
        using var sponsor = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sponsorPublic = sponsor.PublicKey.ExportSubjectPublicKeyInfo();
        using var newcomer = ECDiffieHellman.Create();
        newcomer.ImportSubjectPublicKeyInfo(newcomerPublicKey, out _);
        var wrapKey = GrantKey(sponsor, newcomer, newcomerPublicKey, sponsorPublic);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[roomKey.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(wrapKey, 16))
            aes.Encrypt(nonce, roomKey, ciphertext, tag, newcomerPublicKey);
        CryptographicOperations.ZeroMemory(wrapKey);

        var output = new byte[2 + sponsorPublic.Length + nonce.Length + ciphertext.Length + tag.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)sponsorPublic.Length);
        var offset = 2;
        sponsorPublic.CopyTo(output, offset);
        offset += sponsorPublic.Length;
        nonce.CopyTo(output, offset);
        offset += nonce.Length;
        ciphertext.CopyTo(output, offset);
        offset += ciphertext.Length;
        tag.CopyTo(output, offset);
        return output;
    }

    public static bool TryUnwrapRoomKey(ECDiffieHellman newcomer, byte[] newcomerPublicKey, byte[] blob, out byte[] roomKey)
    {
        roomKey = [];
        try
        {
            if (blob.Length < 2)
                return false;
            var publicLength = BinaryPrimitives.ReadUInt16LittleEndian(blob);
            var offset = 2;
            if (blob.Length < offset + publicLength + 12 + 16)
                return false;
            var sponsorPublic = blob.AsSpan(offset, publicLength).ToArray();
            offset += publicLength;
            var nonce = blob.AsSpan(offset, 12).ToArray();
            offset += 12;
            var ciphertext = blob.AsSpan(offset, blob.Length - offset - 16).ToArray();
            var tag = blob.AsSpan(blob.Length - 16, 16).ToArray();

            using var sponsor = ECDiffieHellman.Create();
            sponsor.ImportSubjectPublicKeyInfo(sponsorPublic, out _);
            var wrapKey = GrantKey(newcomer, sponsor, newcomerPublicKey, sponsorPublic);
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(wrapKey, 16))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, newcomerPublicKey);
            CryptographicOperations.ZeroMemory(wrapKey);
            if (plaintext.Length != KeyBytes)
                return false;
            roomKey = plaintext;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] GrantKey(ECDiffieHellman mine, ECDiffieHellman theirs, byte[] newcomerPublic, byte[] sponsorPublic)
    {
        var shared = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        var salt = SHA256.HashData(Concat(newcomerPublic, sponsorPublic));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeyBytes, salt, GrantInfo);
        CryptographicOperations.ZeroMemory(shared);
        return key;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }
}

public static class Base64Url
{
    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - s.Length % 4) % 4;
        return Convert.FromBase64String(s + new string('=', padding));
    }

    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
            return false;
        try
        {
            bytes = Decode(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
