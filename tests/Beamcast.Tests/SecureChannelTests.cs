using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class SecureChannelTests
{
    [Fact]
    public void SealAndOpenRoundTrip()
    {
        var secret = SecureChannel.NewSecret();
        using var host = SecureChannel.FromSecret(secret);
        using var viewer = SecureChannel.FromSecret(secret);

        var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var framed = host.Seal(MessageType.Video, plaintext, MessageFlags.Keyframe);

        Assert.True(Framing.TryDecodeWhole(framed, out var message));
        Assert.Equal(MessageType.Video, message.Type);
        Assert.True(message.IsEncrypted);
        Assert.True(message.IsKeyframe);
        Assert.NotEqual(plaintext, message.Payload);
        Assert.True(viewer.TryOpen(message, out var opened));
        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void WrongSecretFails()
    {
        using var host = SecureChannel.FromSecret(SecureChannel.NewSecret());
        using var stranger = SecureChannel.FromSecret(SecureChannel.NewSecret());
        var framed = host.Seal(MessageType.Welcome, new byte[] { 42 });
        Framing.TryDecodeWhole(framed, out var message);
        Assert.False(stranger.TryOpen(message, out _));
    }

    [Fact]
    public void TamperedFlagsOrBodyFail()
    {
        var secret = SecureChannel.NewSecret();
        using var host = SecureChannel.FromSecret(secret);
        using var viewer = SecureChannel.FromSecret(secret);
        var framed = host.Seal(MessageType.Video, new byte[] { 1, 2, 3 });

        var flipped = (byte[])framed.Clone();
        flipped[^1] ^= 0x01;
        Framing.TryDecodeWhole(flipped, out var tampered);
        Assert.False(viewer.TryOpen(tampered, out _));

        Framing.TryDecodeWhole(framed, out var original);
        var reflagged = original with { Flags = (byte)(original.Flags | MessageFlags.Keyframe) };
        Assert.False(viewer.TryOpen(reflagged, out _));
    }

    [Fact]
    public void PlaintextIsNotOpened()
    {
        using var viewer = SecureChannel.FromSecret(SecureChannel.NewSecret());
        Framing.TryDecodeWhole(Framing.Encode(MessageType.Bye, ReadOnlySpan<byte>.Empty), out var plain);
        Assert.False(viewer.TryOpen(plain, out _));
    }

    [Fact]
    public void SecretsAreUrlSafeAndRandom()
    {
        var a = SecureChannel.NewSecret();
        var b = SecureChannel.NewSecret();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        Assert.True(a.Length >= 20);
    }
}
