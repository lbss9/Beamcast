using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class RelayProtocolTests
{
    [Fact]
    public void RoomCodesAreValidAndUnambiguous()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = RelayProtocol.NewRoomCode();
            Assert.Equal(RelayProtocol.RoomCodeLength, code.Length);
            Assert.True(RelayProtocol.IsValidRoomCode(code));
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('1', code);
            Assert.DoesNotContain('I', code);
        }
    }

    [Fact]
    public void RoomCodeNormalizationForgivesTyping()
    {
        Assert.Equal("ABC234", RelayProtocol.NormalizeRoomCode(" abc-234 "));
        Assert.False(RelayProtocol.IsValidRoomCode("abc23"));
        Assert.False(RelayProtocol.IsValidRoomCode(null));
    }

    [Fact]
    public void MuxRoundTrips()
    {
        var framed = Framing.Encode(MessageType.Ping, new byte[] { 1, 2, 3 });
        var frame = RelayMux.Encode(42, RelayMux.KindData, framed);

        Assert.True(RelayMux.TryDecode(frame, out var viewerId, out var kind, out var inner));
        Assert.Equal(42u, viewerId);
        Assert.Equal(RelayMux.KindData, kind);
        Assert.Equal(framed, inner.ToArray());
        Assert.False(RelayMux.TryDecode(new byte[] { 1, 2 }, out _, out _, out _));
    }

    [Fact]
    public void JoinMessagesSerializeCamelCase()
    {
        var json = System.Text.Encoding.UTF8.GetString(Json.Serialize(new RelayJoin { Role = "host", AppKey = "k" }));
        Assert.Contains("\"role\":\"host\"", json);
        Assert.Contains("\"appKey\":\"k\"", json);
        var parsed = Json.Deserialize<RelayJoinResult>("{\"ok\":true,\"room\":\"ABC234\"}");
        Assert.NotNull(parsed);
        Assert.True(parsed!.Ok);
        Assert.Equal("ABC234", parsed.Room);
    }
}
