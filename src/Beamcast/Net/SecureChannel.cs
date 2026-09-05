using System.Security.Cryptography;

namespace Beamcast.Net;

/// <summary>
/// End-to-end encryption of every message members exchange. AES-256-GCM with a random 96-bit
/// nonce per message; the message type and flags ride in the clear (and are authenticated as
/// associated data) so the server can route media and apply the keyframe gate without reading
/// anything. The 32-bit birthday bound on random nonces is far beyond what a lounge produces.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private readonly AesGcm _aes;

    public SecureChannel(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        _aes = new AesGcm(key, TagSize);
    }

    /// <summary>Frames and encrypts a message. The type and flags stay readable for the server.</summary>
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

    /// <summary>Convenience: decrypt a framed blob (as stored by the server) into plaintext.</summary>
    public bool TryOpenFramed(ReadOnlySpan<byte> framed, out MessageType type, out byte[] plaintext)
    {
        type = default;
        plaintext = [];
        if (!Framing.TryDecodeWhole(framed, out var message))
            return false;
        type = message.Type;
        return TryOpen(message, out plaintext);
    }

    public void Dispose() => _aes.Dispose();
}
