using System.Security.Cryptography;
using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class FramePathTests
{
    private static SecureChannel NewChannel() => new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void SealWithPrefixIsTheSameMessageAsSeal()
    {
        using var channel = NewChannel();
        var head = new byte[] { 9, 8, 7 };
        var tail = RandomNumberGenerator.GetBytes(5000);

        var frame = channel.SealWithPrefix(LoungeMux.HeaderSize, MessageType.Video, head, tail, MessageFlags.Keyframe);
        Assert.All(frame.Take(LoungeMux.HeaderSize), b => Assert.Equal(0, b)); // room for the transport header

        // The old reader (copying) and the new one (in place) both open it.
        Assert.True(Framing.TryDecodeWhole(frame.AsSpan(LoungeMux.HeaderSize), out var message));
        Assert.True(message.IsKeyframe && message.IsEncrypted);
        Assert.True(channel.TryOpen(message, out var viaOld));
        Assert.True(channel.TryOpenFramed(frame.AsSpan(LoungeMux.HeaderSize), out var type, out var flags, out var viaNew));
        Assert.Equal(MessageType.Video, type);
        Assert.Equal(MessageFlags.Keyframe | MessageFlags.Encrypted, flags);
        Assert.Equal(head.Concat(tail), viaOld);
        Assert.Equal(viaOld, viaNew);
    }

    [Fact]
    public void TryOpenFramedRejectsTamperingTruncationAndTheWrongKey()
    {
        using var channel = NewChannel();
        using var other = NewChannel();
        var framed = channel.Seal(MessageType.Audio, [1, 2, 3, 4]);

        Assert.False(other.TryOpenFramed(framed, out _, out _, out _));
        Assert.False(channel.TryOpenFramed(framed.AsSpan(0, framed.Length - 1), out _, out _, out _));
        var flipped = (byte[])framed.Clone();
        flipped[^1] ^= 1;
        Assert.False(channel.TryOpenFramed(flipped, out _, out _, out _));
        var retyped = (byte[])framed.Clone();
        retyped[Framing.HeaderSize] = (byte)MessageType.Video; // the type is authenticated
        Assert.False(channel.TryOpenFramed(retyped, out _, out _, out _));
        Assert.True(channel.TryOpenFramed(framed, out _, out _, out var plaintext));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, plaintext);
    }

    [Fact]
    public void MuxHeaderReadsBackWithoutCopyingThePayload()
    {
        var frame = LoungeMux.Encode(LoungeMux.Media, 7, 99, [5, 6, 7]);
        Assert.True(LoungeMux.TryReadHeader(frame, out var kind, out var a, out var b, out var payload));
        Assert.Equal((LoungeMux.Media, 7u, 99u), (kind, a, b));
        Assert.True(payload.SequenceEqual(new byte[] { 5, 6, 7 }));
        Assert.False(LoungeMux.TryReadHeader(frame.AsSpan(0, 4), out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(0, 1)]
    public void HalfTheViewersMustComplain(int viewers, int needed) =>
        Assert.Equal(needed, ViewerCongestion.Needed(viewers));

    [Fact]
    public void OneStrugglingViewerAmongFourDoesNotLowerTheBitrate()
    {
        var congestion = new ViewerCongestion();
        Assert.False(congestion.Complain(1, nowMs: 1000, viewerCount: 4));
        Assert.False(congestion.Complain(1, nowMs: 2000, viewerCount: 4)); // the same one again
        Assert.True(congestion.Complain(2, nowMs: 2500, viewerCount: 4));  // now half of them

        // Complaints expire: viewer 2 alone, six seconds later, is one of four again.
        Assert.False(congestion.Complain(2, nowMs: 2500 + ViewerCongestion.WindowMs + 1000, viewerCount: 4));

        // Alone, or one of two, a single complaint is enough.
        Assert.True(new ViewerCongestion().Complain(1, 0, viewerCount: 1));
        Assert.True(new ViewerCongestion().Complain(1, 0, viewerCount: 2));

        congestion.Reset();
        Assert.False(congestion.Complain(3, 0, viewerCount: 3));
        congestion.Forget(3);
        Assert.False(congestion.Complain(4, 1, viewerCount: 3));
    }
}
