using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class ProtocolTests
{
    [Fact]
    public void FramingRoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var framed = Framing.Encode(MessageType.Video, payload);

        Assert.Equal(Framing.HeaderSize + 1 + payload.Length, framed.Length);
        Assert.True(Framing.TryDecode(framed, out var type, out var decoded, out var consumed));
        Assert.Equal(MessageType.Video, type);
        Assert.Equal(payload, decoded);
        Assert.Equal(framed.Length, consumed);
    }

    [Fact]
    public void FramingHandlesEmptyPayload()
    {
        var framed = Framing.Encode(MessageType.KeyframeRequest, ReadOnlySpan<byte>.Empty);
        Assert.True(Framing.TryDecode(framed, out var type, out var payload, out _));
        Assert.Equal(MessageType.KeyframeRequest, type);
        Assert.Empty(payload);
    }

    [Fact]
    public void FramingWaitsForWholeMessage()
    {
        var framed = Framing.Encode(MessageType.Hello, new byte[] { 9, 9, 9 });
        Assert.False(Framing.TryDecode(framed.AsSpan(0, framed.Length - 1), out _, out _, out _));
    }

    [Fact]
    public void FramingRejectsHostileLengths()
    {
        Assert.False(Framing.TryReadLength(new byte[] { 0, 0, 0, 0 }, out _));
        Assert.False(Framing.TryReadLength(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, out _));
        Assert.False(Framing.TryReadLength(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, out _));
    }

    [Fact]
    public void VideoPacketRoundTrips()
    {
        var header = new VideoPacketHeader(42, 1_700_000_000_123, 1920, 1080, true);
        var bitstream = new byte[] { 0x10, 0x20, 0x30 };
        var body = VideoPacket.Build(header, bitstream);

        Assert.True(VideoPacket.TryParse(body, out var parsed, out var data));
        Assert.Equal(header, parsed);
        Assert.Equal(bitstream, data.ToArray());
    }

    [Fact]
    public void VideoPacketRejectsEmptyBitstream()
    {
        var body = VideoPacket.Build(new VideoPacketHeader(1, 0, 4, 4, false), ReadOnlySpan<byte>.Empty);
        Assert.False(VideoPacket.TryParse(body, out _, out _));
    }

    [Fact]
    public async Task MessageStreamRoundTripsOverAPipe()
    {
        using var pipe = new MemoryStream();
        await MessageStream.WriteJsonAsync(pipe, MessageType.Hello, new HelloMessage { Name = "Ana" }, CancellationToken.None);
        await MessageStream.WriteAsync(pipe, MessageType.Ping, new byte[] { 7 }, CancellationToken.None);
        pipe.Position = 0;

        var first = await MessageStream.ReadAsync(pipe, CancellationToken.None);
        var second = await MessageStream.ReadAsync(pipe, CancellationToken.None);
        var end = await MessageStream.ReadAsync(pipe, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(MessageType.Hello, first.Value.Type);
        Assert.Equal("Ana", Json.Deserialize<HelloMessage>(first.Value.Payload)?.Name);
        Assert.NotNull(second);
        Assert.Equal(MessageType.Ping, second.Value.Type);
        Assert.Equal(new byte[] { 7 }, second.Value.Payload);
        Assert.Null(end);
    }

    [Fact]
    public void AuthProofVerifiesAndRejects()
    {
        var nonce = AuthProof.NewNonce();
        var proof = AuthProof.Compute("hunter2", nonce);

        Assert.True(AuthProof.Verify("hunter2", nonce, proof));
        Assert.False(AuthProof.Verify("hunter3", nonce, proof));
        Assert.False(AuthProof.Verify("hunter2", AuthProof.NewNonce(), proof));
        Assert.False(AuthProof.Verify("hunter2", nonce, null));
        Assert.False(AuthProof.Verify("hunter2", nonce, ""));
    }
}
