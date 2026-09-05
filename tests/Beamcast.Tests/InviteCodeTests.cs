using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class InviteCodeTests
{
    [Fact]
    public void RoundTripsDirectTargetWithSecretAndPassword()
    {
        var code = InviteCode.Encode(InviteTarget.Direct("203.0.113.9", 47700, "s3cr3t_X", "s3cret|pipe"));

        Assert.StartsWith("BC-", code);
        Assert.True(InviteCode.TryDecode(code, out var target));
        Assert.Equal(InviteKind.Direct, target.Kind);
        Assert.Equal("203.0.113.9", target.Host);
        Assert.Equal(47700, target.Port);
        Assert.Equal("s3cret|pipe", target.Password);
        Assert.Equal("s3cr3t_X", target.Secret);
    }

    [Fact]
    public void RoundTripsRelayTarget()
    {
        var secret = SecureChannel.NewSecret();
        var code = InviteCode.Encode(InviteTarget.Relay("wss://relay.example.com/ws", "ABC234", secret, null));

        Assert.True(InviteCode.TryDecode(code, out var target));
        Assert.Equal(InviteKind.Relay, target.Kind);
        Assert.Equal("wss://relay.example.com/ws", target.RelayUrl);
        Assert.Equal("ABC234", target.Room);
        Assert.Equal(secret, target.Secret);
        Assert.Null(target.Password);
        Assert.True(target.HasSecret);
    }

    [Fact]
    public void RelayTargetNeedsValidRoomAndSecret()
    {
        Assert.False(InviteCode.TryDecode(InviteCode.Encode(InviteTarget.Relay("wss://r/ws", "ABC", "x", null)), out _));
        Assert.False(InviteCode.TryDecode(InviteCode.Encode(InviteTarget.Relay("wss://r/ws", "ABC234", "", null)), out _));
        Assert.False(InviteCode.TryDecode(InviteCode.Encode(InviteTarget.Relay("http://r/ws", "ABC234", "x", null)), out _));
    }

    [Fact]
    public void StillReadsVersionOneCodes()
    {
        var legacy = "BC-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("meu-pc.local|5000|")).TrimEnd('=');
        Assert.True(InviteCode.TryDecode(legacy, out var target));
        Assert.Equal("meu-pc.local", target.Host);
        Assert.Equal(5000, target.Port);
        Assert.Null(target.Password);
        Assert.Null(target.Secret);
        Assert.False(target.HasSecret);
    }

    [Theory]
    [InlineData("192.168.0.10", "192.168.0.10", AppInfo.DefaultPort)]
    [InlineData("192.168.0.10:6000", "192.168.0.10", 6000)]
    [InlineData("  host.example.com:81 ", "host.example.com", 81)]
    [InlineData("[fe80::1]:9000", "fe80::1", 9000)]
    [InlineData("[::1]", "::1", AppInfo.DefaultPort)]
    public void AcceptsPlainAddresses(string input, string host, int port)
    {
        Assert.True(InviteCode.TryDecode(input, out var target));
        Assert.Equal(host, target.Host);
        Assert.Equal(port, target.Port);
        Assert.Null(target.Password);
        Assert.False(target.HasSecret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BC-")]
    [InlineData("BC-!!!!")]
    [InlineData("host:notaport")]
    [InlineData("host:70000")]
    [InlineData("[fe80::1")]
    [InlineData("has space:80")]
    public void RejectsGarbage(string input)
    {
        Assert.False(InviteCode.TryDecode(input, out _));
    }

    [Fact]
    public void RelayUrlValidation()
    {
        Assert.True(InviteCode.IsValidRelayUrl("wss://relay.example.com/ws"));
        Assert.True(InviteCode.IsValidRelayUrl("ws://localhost:5092/ws"));
        Assert.False(InviteCode.IsValidRelayUrl("https://relay.example.com/ws"));
        Assert.False(InviteCode.IsValidRelayUrl("relay.example.com"));
        Assert.False(InviteCode.IsValidRelayUrl(null));
    }
}
