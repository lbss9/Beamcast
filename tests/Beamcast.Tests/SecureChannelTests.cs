using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class SecureChannelTests
{
    private static byte[] Key(byte fill)
    {
        var key = new byte[32];
        Array.Fill(key, fill);
        return key;
    }

    [Fact]
    public void SealAndOpenRoundTrip()
    {
        using var host = new SecureChannel(Key(1));
        using var viewer = new SecureChannel(Key(1));

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
    public void WrongKeyFails()
    {
        using var host = new SecureChannel(Key(1));
        using var stranger = new SecureChannel(Key(2));
        var framed = host.Seal(MessageType.Presence, new byte[] { 42 });
        Framing.TryDecodeWhole(framed, out var message);
        Assert.False(stranger.TryOpen(message, out _));
    }

    [Fact]
    public void TamperedFlagsOrBodyFail()
    {
        using var host = new SecureChannel(Key(3));
        using var viewer = new SecureChannel(Key(3));
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
        using var viewer = new SecureChannel(Key(4));
        Framing.TryDecodeWhole(Framing.Encode(MessageType.Audio, ReadOnlySpan<byte>.Empty), out var plain);
        Assert.False(viewer.TryOpen(plain, out _));
    }

    [Fact]
    public void RejectsWrongKeySize()
    {
        Assert.Throws<ArgumentException>(() => new SecureChannel(new byte[16]));
    }
}
