using System.Security.Cryptography;
using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class RoomProtocolTests
{
    [Fact]
    public void CodesHaveTheRequestedLengthAndAlphabet()
    {
        var publicCode = LoungeProtocol.NewCode(LoungeProtocol.PublicCodeLength);
        var privateCode = LoungeProtocol.NewCode(LoungeProtocol.PrivateCodeLength);
        Assert.Equal(6, publicCode.Length);
        Assert.Equal(10, privateCode.Length);
        Assert.True(LoungeProtocol.IsValidCode(publicCode));
        Assert.True(LoungeProtocol.IsValidCode(privateCode));
        Assert.DoesNotContain('0', privateCode);
        Assert.DoesNotContain('O', privateCode);
        Assert.False(LoungeProtocol.IsValidCode("ABC"));
        Assert.False(LoungeProtocol.IsValidCode(new string('A', 13)));
    }

    [Theory]
    [InlineData("ws://192.168.1.20:47710/ws", "http://192.168.1.20:47710/rooms")]
    [InlineData("wss://beamcast.example.com/ws", "https://beamcast.example.com/rooms")]
    [InlineData("wss://beamcast.example.com:8443/beam/ws", "https://beamcast.example.com:8443/beam/rooms")]
    public void HttpUrlSitsNextToTheWebSocketEndpoint(string ws, string expected)
    {
        Assert.Equal(expected, LoungeProtocol.HttpUrl(ws, LoungeProtocol.RoomsPath));
    }

    [Theory]
    [InlineData("ws://192.168.1.20:47710/ws", "192.168.1.20")]
    [InlineData("ws://192.168.1.20:5000/ws", "192.168.1.20:5000")]
    [InlineData("wss://beamcast.example.com/ws", "beamcast.example.com")]
    public void DisplayHostHidesDefaultPorts(string url, string expected)
    {
        Assert.Equal(expected, LoungeProtocol.DisplayHost(url));
    }

    [Fact]
    public void TtlAndMaxMembersAreClamped()
    {
        Assert.Equal(LoungeProtocol.DefaultTtlHours, LoungeProtocol.ClampTtlHours(0));
        Assert.Equal(LoungeProtocol.DefaultTtlHours, LoungeProtocol.ClampTtlHours(double.NaN));
        Assert.Equal(LoungeProtocol.MaxTtlHours, LoungeProtocol.ClampTtlHours(1e9));
        Assert.Equal(0, LoungeProtocol.ClampMaxMembers(-5));
        Assert.Equal(2, LoungeProtocol.ClampMaxMembers(1));
        Assert.Equal(LoungeProtocol.MaxMembersCap, LoungeProtocol.ClampMaxMembers(1000));
    }

    [Fact]
    public void EnumsNormalizeToKnownValues()
    {
        Assert.Equal(RoomVisibility.Public, RoomVisibility.Normalize("PUBLIC"));
        Assert.Equal(RoomVisibility.Private, RoomVisibility.Normalize("whatever"));
        Assert.Equal(RoomKind.Temporary, RoomKind.Normalize("temporary"));
        Assert.Equal(RoomKind.Permanent, RoomKind.Normalize(null));
        Assert.Equal(BroadcastPolicy.Owner, BroadcastPolicy.Normalize("owner"));
        Assert.Equal(BroadcastPolicy.Everyone, BroadcastPolicy.Normalize(""));
    }

    [Fact]
    public void HandshakeRepliesRoundTripThroughJson()
    {
        var welcome = new LoungeReply
        {
            Ok = true,
            Stage = LoungeReply.StageWelcome,
            MemberId = 7,
            IsOwner = true,
            NeedsKey = false,
            OwnerToken = "tok",
            Room = new RoomInfo { Code = "ABCDEF", Name = "Sala", Visibility = RoomVisibility.Public, HasPassword = true, Members = 3 },
            Members = [new LoungeMemberInfo { Id = 2, IsOwner = false, Presence = "AAA=" }],
        };
        var parsed = Json.Deserialize<LoungeReply>(Json.Serialize(welcome));
        Assert.NotNull(parsed);
        Assert.Equal(7u, parsed!.MemberId);
        Assert.True(parsed.IsOwner);
        Assert.Equal("ABCDEF", parsed.Room!.Code);
        Assert.True(parsed.Room.HasPassword);
        Assert.Equal(3, parsed.Room.Members);
        Assert.Single(parsed.Members);
    }

    [Fact]
    public void MuxKindsAreDistinct()
    {
        var kinds = new[]
        {
            LoungeMux.Control, LoungeMux.Publish, LoungeMux.Unpublish, LoungeMux.Media, LoungeMux.Subscribe, LoungeMux.Unsubscribe,
            LoungeMux.KeyframeRequest, LoungeMux.Presence, LoungeMux.MemberJoined, LoungeMux.MemberLeft, LoungeMux.StreamStarted,
            LoungeMux.StreamEnded, LoungeMux.PublishAck, LoungeMux.Heartbeat, LoungeMux.KeyRequest, LoungeMux.KeyGrant,
            LoungeMux.RoomUpdate, LoungeMux.RoomInfo, LoungeMux.InviteCreate, LoungeMux.InviteCreated, LoungeMux.InviteRevokeAll,
            LoungeMux.Kick, LoungeMux.RoomDelete, LoungeMux.Notice, LoungeMux.Bye,
        };
        Assert.Equal(kinds.Length, kinds.Distinct().Count());
    }
}

public class RoomCryptoTests
{
    [Fact]
    public void RoomKeyTravelsToTheNewcomerAndOnlyToIt()
    {
        var roomKey = LoungeCrypto.NewRoomKey();
        using var newcomer = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var newcomerPublic = newcomer.PublicKey.ExportSubjectPublicKeyInfo();

        var blob = LoungeCrypto.WrapRoomKey(roomKey, newcomerPublic);
        Assert.True(LoungeCrypto.TryUnwrapRoomKey(newcomer, newcomerPublic, blob, out var unwrapped));
        Assert.Equal(roomKey, unwrapped);

        // Somebody else (the server, say) holding a different key pair learns nothing.
        using var eavesdropper = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(LoungeCrypto.TryUnwrapRoomKey(eavesdropper, eavesdropper.PublicKey.ExportSubjectPublicKeyInfo(), blob, out _));

        // Tampering is detected.
        blob[^1] ^= 0x01;
        Assert.False(LoungeCrypto.TryUnwrapRoomKey(newcomer, newcomerPublic, blob, out _));
        Assert.False(LoungeCrypto.TryUnwrapRoomKey(newcomer, newcomerPublic, [1, 2, 3], out _));
    }

    [Fact]
    public void TokensAreCheckedByHashOnly()
    {
        var token = LoungeCrypto.NewToken();
        var hash = LoungeCrypto.TokenHash(token);
        Assert.NotEqual(token, hash);
        Assert.True(LoungeCrypto.TokenMatches(hash, token));
        Assert.False(LoungeCrypto.TokenMatches(hash, token + "x"));
        Assert.False(LoungeCrypto.TokenMatches(hash, null));
        Assert.False(LoungeCrypto.TokenMatches(string.Empty, token));
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
    }

    [Fact]
    public void PasswordProofNeverRevealsTheContentKey()
    {
        var salt = LoungeCrypto.NewSalt();
        var key = LoungeCrypto.DeriveKey("correct horse", salt);
        var verifier = LoungeCrypto.Verifier(key);
        var nonce = LoungeCrypto.NewNonce();
        var proof = LoungeCrypto.Proof(verifier, nonce);
        Assert.True(LoungeCrypto.VerifyProof(verifier, nonce, proof));
        Assert.False(LoungeCrypto.VerifyProof(verifier, LoungeCrypto.NewNonce(), proof));
        var wrong = LoungeCrypto.Verifier(LoungeCrypto.DeriveKey("wrong", salt));
        Assert.False(LoungeCrypto.VerifyProof(wrong, nonce, proof));
        Assert.NotEqual(LoungeCrypto.ContentKey(key), verifier);
    }
}

public class InviteV3Tests
{
    private const string Server = "wss://beamcast.example.com/ws";

    [Fact]
    public void InviteCarriesTokenAndKeyAndComesBackIntact()
    {
        var key = LoungeCrypto.NewRoomKey();
        var invite = LoungeInvite.Encode(new LoungeTarget(Server, "ABCDEFGHJK", "tok_123", key));
        Assert.StartsWith("BC-", invite);
        Assert.True(LoungeInvite.TryDecode(invite, "ws://other", out var target));
        Assert.Equal(Server, target.ServerUrl);
        Assert.Equal("ABCDEFGHJK", target.Code);
        Assert.Equal("tok_123", target.InviteToken);
        Assert.Equal(key, target.ContentKey);
    }

    [Fact]
    public void PlainPointerHasNeitherTokenNorKey()
    {
        var invite = LoungeInvite.Encode(new LoungeTarget(Server, "ABC234"));
        Assert.True(LoungeInvite.TryDecode(invite, string.Empty, out var target));
        Assert.Null(target.InviteToken);
        Assert.Null(target.ContentKey);
    }

    [Fact]
    public void BareCodesAndLegacyInvitesStillDecode()
    {
        Assert.True(LoungeInvite.TryDecode(" abc234 ", "192.168.1.20", out var bare));
        Assert.Equal("ws://192.168.1.20:47710/ws", bare.ServerUrl);
        Assert.Equal("ABC234", bare.Code);

        var legacy = "BC-" + Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(Server + "|ABC234"));
        Assert.True(LoungeInvite.TryDecode(legacy, string.Empty, out var old));
        Assert.Equal("ABC234", old.Code);

        Assert.False(LoungeInvite.TryDecode("BC-!!!", "192.168.1.20", out _));
        Assert.False(LoungeInvite.TryDecode("", "192.168.1.20", out _));
        Assert.False(LoungeInvite.TryDecode("BC-" + Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes("3|" + Server + "|ABC234||notakey")), string.Empty, out _));
    }
}

public class JoinRateLimiterTests
{
    [Fact]
    public void BlocksAfterTooManyFailuresInsideTheWindow()
    {
        var limiter = new JoinRateLimiter(3, TimeSpan.FromMinutes(10));
        Assert.False(limiter.IsBlocked("a", 0));
        limiter.RecordFailure("a", 0);
        limiter.RecordFailure("a", 1000);
        Assert.False(limiter.IsBlocked("a", 2000));
        limiter.RecordFailure("a", 2000);
        Assert.True(limiter.IsBlocked("a", 3000));
        Assert.False(limiter.IsBlocked("b", 3000));
    }

    [Fact]
    public void FailuresExpireAndClearResets()
    {
        var limiter = new JoinRateLimiter(2, TimeSpan.FromSeconds(10));
        limiter.RecordFailure("a", 0);
        limiter.RecordFailure("a", 100);
        Assert.True(limiter.IsBlocked("a", 200));
        Assert.False(limiter.IsBlocked("a", 11_000));
        limiter.RecordFailure("a", 11_000);
        limiter.RecordFailure("a", 11_100);
        Assert.True(limiter.IsBlocked("a", 11_200));
        limiter.Clear("a");
        Assert.False(limiter.IsBlocked("a", 11_300));
    }
}
