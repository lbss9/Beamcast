using System.Security.Cryptography;
using System.Text;

namespace Beamcast.Net;

/// <summary>
/// End-to-end encryption for everything after the first handshake message. The key is derived from
/// the room secret that travels inside the invite code, so the relay (and anything between the two
/// machines) only ever sees message types and flags. AES-256-GCM with a random 96-bit nonce per
/// message; the 32-bit birthday bound is far beyond what a screen-share session produces.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int SecretBytes = 16;

    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("beamcast/e2e/v2");

    private readonly AesGcm _aes;

    private SecureChannel(byte[] key)
    {
        _aes = new AesGcm(key, TagSize);
    }

    /// <summary>Derives the channel from the invite secret and the optional room password.</summary>
    public static SecureChannel FromSecret(string secret, string? password = null)
    {
        var ikm = Encoding.UTF8.GetBytes((secret ?? string.Empty) + "\n" + (password ?? string.Empty));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: KeyInfo);
        return new SecureChannel(key);
    }

    /// <summary>A fresh random room secret, URL-safe so it fits in the invite code.</summary>
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Frames and encrypts a message. The type and flags stay readable for the relay.</summary>
    public byte[] Seal(MessageType type, ReadOnlySpan<byte> plaintext, byte flags = MessageFlags.None)
    {
        var body = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = body.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = body.AsSpan(NonceSize, plaintext.Length);
        var tag = body.AsSpan(NonceSize + plaintext.Length, TagSize);
        var aad = new byte[] { (byte)type, (byte)(flags | MessageFlags.Encrypted) };
        lock (_aes)
        {
            _aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        return Framing.Encode(type, body, (byte)(flags | MessageFlags.Encrypted));
    }

    /// <summary>Decrypts a message body; returns false when the key is wrong or the data was tampered with.</summary>
    public bool TryOpen(Message message, out byte[] plaintext)
    {
        plaintext = [];
        if (!message.IsEncrypted || message.Payload.Length < NonceSize + TagSize)
            return false;

        var body = message.Payload.AsSpan();
        var nonce = body[..NonceSize];
        var ciphertext = body.Slice(NonceSize, body.Length - NonceSize - TagSize);
        var tag = body[^TagSize..];
        var output = new byte[ciphertext.Length];
        var aad = new byte[] { (byte)message.Type, message.Flags };
        try
        {
            lock (_aes)
            {
                _aes.Decrypt(nonce, ciphertext, tag, output, aad);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
        plaintext = output;
        return true;
    }

    public void Dispose() => _aes.Dispose();
}
