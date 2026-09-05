using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class ProtocolTests
{
    [Fact]
    public void FramingRoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var framed = Framing.Encode(MessageType.Video, payload, MessageFlags.Keyframe);

        Assert.Equal(Framing.HeaderSize + Framing.PrefixSize + payload.Length, framed.Length);
        Assert.True(Framing.TryDecode(framed, out var message, out var consumed));
        Assert.Equal(MessageType.Video, message.Type);
        Assert.True(message.IsKeyframe);
        Assert.False(message.IsEncrypted);
        Assert.Equal(payload, message.Payload);
        Assert.Equal(framed.Length, consumed);
        Assert.True(Framing.TryPeek(framed, out var type, out var flags));
        Assert.Equal(MessageType.Video, type);
        Assert.Equal(MessageFlags.Keyframe, flags);
    }

    [Fact]
    public void FramingHandlesEmptyPayload()
    {
        var framed = Framing.Encode(MessageType.Presence, ReadOnlySpan<byte>.Empty);
        Assert.True(Framing.TryDecodeWhole(framed, out var message));
        Assert.Equal(MessageType.Presence, message.Type);
        Assert.Empty(message.Payload);
    }

    [Fact]
    public void FramingWaitsForWholeMessage()
    {
        var framed = Framing.Encode(MessageType.StreamMeta, new byte[] { 9, 9, 9 });
        Assert.False(Framing.TryDecode(framed.AsSpan(0, framed.Length - 1), out _, out _));
        Assert.False(Framing.TryDecodeWhole(framed.Concat(new byte[] { 1 }).ToArray(), out _));
    }

    [Fact]
    public void FramingRejectsHostileLengths()
    {
        Assert.False(Framing.TryReadLength(new byte[] { 0, 0, 0, 0 }, out _));
        Assert.False(Framing.TryReadLength(new byte[] { 1, 0, 0, 0 }, out _));
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
    public void AudioPacketRoundTrips()
    {
        var header = new AudioPacketHeader(7, 123456, 48000, 2);
        var body = AudioPacket.Build(header, new byte[] { 0xAA, 0xBB });
        Assert.True(AudioPacket.TryParse(body, out var parsed, out var opus));
        Assert.Equal(header, parsed);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, opus.ToArray());
        Assert.False(AudioPacket.TryParse(AudioPacket.Build(header, ReadOnlySpan<byte>.Empty), out _, out _));
    }

    [Fact]
    public void VideoPacketRejectsEmptyBitstream()
    {
        var body = VideoPacket.Build(new VideoPacketHeader(1, 0, 4, 4, false), ReadOnlySpan<byte>.Empty);
        Assert.False(VideoPacket.TryParse(body, out _, out _));
    }


}
