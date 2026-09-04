using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class InviteCodeTests
{
    [Fact]
    public void RoundTripsHostPortAndPassword()
    {
        var code = InviteCode.Encode(new InviteTarget("203.0.113.9", 47700, "s3cret|pipe"));

        Assert.StartsWith("BC-", code);
        Assert.True(InviteCode.TryDecode(code, out var target));
        Assert.Equal("203.0.113.9", target.Host);
        Assert.Equal(47700, target.Port);
        Assert.Equal("s3cret|pipe", target.Password);
    }

    [Fact]
    public void RoundTripsWithoutPassword()
    {
        var code = InviteCode.Encode(new InviteTarget("meu-pc.local", 5000, null));

        Assert.True(InviteCode.TryDecode(code, out var target));
        Assert.Equal("meu-pc.local", target.Host);
        Assert.Equal(5000, target.Port);
        Assert.Null(target.Password);
        Assert.False(target.HasPassword);
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
    public void DecodedCodeWithBadPortIsRejected()
    {
        var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("host|99999|x")).TrimEnd('=');
        Assert.False(InviteCode.TryDecode("BC-" + raw, out _));
    }
}
