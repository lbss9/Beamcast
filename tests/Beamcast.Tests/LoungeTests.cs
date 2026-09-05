using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class LoungeCryptoTests
{
    [Fact]
    public void SamePasswordSameSaltSameKeys()
    {
        var salt = LoungeCrypto.NewSalt();
        var a = LoungeCrypto.DeriveKey("segredo", salt);
        var b = LoungeCrypto.DeriveKey("segredo", salt);
        Assert.Equal(a, b);
        Assert.Equal(LoungeCrypto.ContentKey(a), LoungeCrypto.ContentKey(b));
        Assert.Equal(LoungeCrypto.Verifier(a), LoungeCrypto.Verifier(b));
    }

    [Fact]
    public void DifferentPasswordOrSaltDiffers()
    {
        var salt = LoungeCrypto.NewSalt();
        var a = LoungeCrypto.DeriveKey("segredo", salt);
        Assert.NotEqual(a, LoungeCrypto.DeriveKey("segredo2", salt));
        Assert.NotEqual(a, LoungeCrypto.DeriveKey("segredo", LoungeCrypto.NewSalt()));
        Assert.NotEqual(LoungeCrypto.Verifier(a), LoungeCrypto.ContentKey(a));
    }

    [Fact]
    public void ProofChecksOutOnlyWithMatchingVerifierAndNonce()
    {
        var key = LoungeCrypto.DeriveKey("pw", LoungeCrypto.NewSalt());
        var verifier = LoungeCrypto.Verifier(key);
        var nonce = LoungeCrypto.NewNonce();
        var proof = LoungeCrypto.Proof(verifier, nonce);

        Assert.True(LoungeCrypto.VerifyProof(verifier, nonce, proof));
        Assert.False(LoungeCrypto.VerifyProof(verifier, LoungeCrypto.NewNonce(), proof));
        Assert.False(LoungeCrypto.VerifyProof(LoungeCrypto.Verifier(LoungeCrypto.DeriveKey("other", LoungeCrypto.NewSalt())), nonce, proof));
        Assert.False(LoungeCrypto.VerifyProof(verifier, nonce, "not base64!"));
        Assert.False(LoungeCrypto.VerifyProof(verifier, nonce, null));
    }

    [Fact]
    public void ContentKeyDrivesTheChannel()
    {
        var salt = LoungeCrypto.NewSalt();
        using var alice = new SecureChannel(LoungeCrypto.ContentKey(LoungeCrypto.DeriveKey("pw", salt)));
        using var bob = new SecureChannel(LoungeCrypto.ContentKey(LoungeCrypto.DeriveKey("pw", salt)));
        using var eve = new SecureChannel(LoungeCrypto.ContentKey(LoungeCrypto.DeriveKey("PW", salt)));

        var framed = alice.Seal(MessageType.Presence, new byte[] { 1, 2, 3 });
        Assert.True(bob.TryOpenFramed(framed, out var type, out var plain));
        Assert.Equal(MessageType.Presence, type);
        Assert.Equal(new byte[] { 1, 2, 3 }, plain);
        Assert.False(eve.TryOpenFramed(framed, out _, out _));
    }
}

public class LoungeProtocolTests
{
    [Fact]
    public void CodesAreValidAndUnambiguous()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = LoungeProtocol.NewCode();
            Assert.True(LoungeProtocol.IsValidCode(code));
            Assert.DoesNotContain('0', code);
            Assert.DoesNotContain('O', code);
            Assert.DoesNotContain('1', code);
            Assert.DoesNotContain('I', code);
        }
        Assert.Equal("ABC234", LoungeProtocol.NormalizeCode(" abc-234 "));
        Assert.False(LoungeProtocol.IsValidCode("abc23"));
    }

    [Theory]
    [InlineData("192.168.1.20", "ws://192.168.1.20:47710/ws")]
    [InlineData("192.168.1.20:5000", "ws://192.168.1.20:5000/ws")]
    [InlineData("beamcast.example.com", "ws://beamcast.example.com:47710/ws")]
    [InlineData("wss://beamcast.example.com", "wss://beamcast.example.com/ws")]
    [InlineData("https://beamcast.example.com/", "wss://beamcast.example.com/ws")]
    [InlineData("ws://10.0.0.2:47710/ws", "ws://10.0.0.2:47710/ws")]
    public void ServerAddressesAreNormalized(string input, string expected)
    {
        Assert.True(LoungeProtocol.TryNormalizeServer(input, out var url));
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ws://")]
    public void BadServerAddressesAreRejected(string input)
    {
        Assert.False(LoungeProtocol.TryNormalizeServer(input, out _));
    }

    [Fact]
    public void MuxRoundTrips()
    {
        var payload = new byte[] { 9, 8, 7 };
        var frame = LoungeMux.Encode(LoungeMux.Media, 42, 7, payload);
        Assert.True(LoungeMux.TryDecode(frame, out var kind, out var a, out var b, out var inner));
        Assert.Equal(LoungeMux.Media, kind);
        Assert.Equal(42u, a);
        Assert.Equal(7u, b);
        Assert.Equal(payload, inner);
        Assert.False(LoungeMux.TryDecode(new byte[] { 1, 2 }, out _, out _, out _, out _));
    }

    [Fact]
    public void InviteRoundTripsAndAcceptsBareCodes()
    {
        var code = LoungeInvite.Encode(new LoungeTarget("ws://192.168.1.20:47710/ws", "ABC234"));
        Assert.StartsWith("BC-", code);
        Assert.True(LoungeInvite.TryDecode(code, "ws://other:1/ws", out var target));
        Assert.Equal("ws://192.168.1.20:47710/ws", target.ServerUrl);
        Assert.Equal("ABC234", target.Code);

        Assert.True(LoungeInvite.TryDecode(" abc234 ", "192.168.1.20", out var bare));
        Assert.Equal("ws://192.168.1.20:47710/ws", bare.ServerUrl);
        Assert.Equal("ABC234", bare.Code);

        Assert.True(LoungeInvite.TryDecode("beamcast.example.com ABC234", "", out var pair));
        Assert.Equal("ws://beamcast.example.com:47710/ws", pair.ServerUrl);

        Assert.False(LoungeInvite.TryDecode("ABC234", "", out _));
        Assert.False(LoungeInvite.TryDecode("BC-!!!", "192.168.1.20", out _));
        Assert.False(LoungeInvite.TryDecode("", "192.168.1.20", out _));
    }
}
