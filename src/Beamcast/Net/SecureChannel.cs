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
    public byte[] Seal(MessageType type, ReadOnlySpan<byte> plaintext, byte flags = MessageFlags.None) =>
        SealWithPrefix(0, type, plaintext, ReadOnlySpan<byte>.Empty, flags);

    /// <summary>
    /// Same as <see cref="Seal"/>, built in a single allocation with room in front for a transport
    /// header: <c>[prefixSize bytes, left zeroed][framed encrypted message]</c>. The plaintext is
    /// <paramref name="head"/> followed by <paramref name="tail"/> (a packet header and its
    /// bitstream), copied once into place and encrypted there. A 4K keyframe used to be copied
    /// four times on its way out; this is the one copy that is left.
    /// </summary>
    public byte[] SealWithPrefix(int prefixSize, MessageType type, ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, byte flags = MessageFlags.None)
    {
        var plainLength = head.Length + tail.Length;
        var bodyLength = NonceSize + plainLength + TagSize;
        if (bodyLength + Framing.PrefixSize > Framing.MaxMessageSize)
            throw new ArgumentException("Message too large.", nameof(tail));
        flags |= MessageFlags.Encrypted;

        var buffer = new byte[prefixSize + Framing.HeaderSize + Framing.PrefixSize + bodyLength];
        var framed = buffer.AsSpan(prefixSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(framed, bodyLength + Framing.PrefixSize);
        framed[Framing.HeaderSize] = (byte)type;
        framed[Framing.HeaderSize + 1] = flags;

        var body = framed[(Framing.HeaderSize + Framing.PrefixSize)..];
        var nonce = body[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var text = body.Slice(NonceSize, plainLength);
        head.CopyTo(text);
        tail.CopyTo(text[head.Length..]);
        var tag = body.Slice(NonceSize + plainLength, TagSize);
        Span<byte> aad = [(byte)type, flags];
        lock (_aes)
        {
            _aes.Encrypt(nonce, text, text, tag, aad); // in place: same span for plaintext and ciphertext
        }
        return buffer;
    }

    /// <summary>
    /// Opens a framed message straight from the bytes it arrived in: no intermediate copies, the
    /// only allocation is the plaintext. False when malformed, tampered with or the key is wrong.
    /// </summary>
    public bool TryOpenFramed(ReadOnlySpan<byte> framed, out MessageType type, out byte flags, out byte[] plaintext)
    {
        type = default;
        flags = 0;
        plaintext = [];
        if (!Framing.TryReadLength(framed, out var length) || framed.Length != Framing.HeaderSize + length)
            return false;
        type = (MessageType)framed[Framing.HeaderSize];
        flags = framed[Framing.HeaderSize + 1];
        var body = framed[(Framing.HeaderSize + Framing.PrefixSize)..];
        if ((flags & MessageFlags.Encrypted) == 0 || body.Length < NonceSize + TagSize)
            return false;

        var output = new byte[body.Length - NonceSize - TagSize];
        Span<byte> aad = [(byte)type, flags];
        try
        {
            lock (_aes)
            {
                _aes.Decrypt(body[..NonceSize], body.Slice(NonceSize, output.Length), body[^TagSize..], output, aad);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
        plaintext = output;
        return true;
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
    public bool TryOpenFramed(ReadOnlySpan<byte> framed, out MessageType type, out byte[] plaintext) =>
        TryOpenFramed(framed, out type, out _, out plaintext);

    public void Dispose() => _aes.Dispose();
}
