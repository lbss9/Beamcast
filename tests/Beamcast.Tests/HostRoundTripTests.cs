using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

/// <summary>
/// The app's client against the real host (see <see cref="HostFixture"/>): the paths a protocol
/// change is most likely to break, exercised end to end on this machine.
/// </summary>
public class HostRoundTripTests : IClassFixture<HostFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly HostFixture _host;

    public HostRoundTripTests(HostFixture host) => _host = host;

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationToken Timeout() => new CancellationTokenSource(Wait).Token;

    [Fact]
    public async Task RoomWithoutPassword_KeyHandoff_Publish_Watch_Update_Signal()
    {
        using var publisher = await LoungeClient.CreateAsync(_host.Url, new RoomCreateOptions { Name = "round trip" }, null, Timeout());
        Assert.True(publisher.IsOwner);

        var listed = await LoungeClient.ListRoomsAsync(_host.Url, null, Timeout());
        Assert.Equal("test-host", listed.Name);
        Assert.Contains(listed.Rooms, r => r.Code == publisher.Code && !r.HasPassword);

        // No password: the newcomer gets the room key from the member inside, wrapped for it.
        using var viewer = await LoungeClient.JoinAsync(_host.Url, new RoomJoinOptions { Code = publisher.Code }, null, Timeout());
        Assert.Equal(publisher.ContentKey, viewer.ContentKey);

        var started = Signal<(uint Stream, uint Owner, StreamMetaMessage Meta)>();
        viewer.StreamStarted += (stream, owner, meta) => started.TrySetResult((stream, owner, meta));
        var streamId = await publisher.PublishAsync(new StreamMetaMessage { Title = "tela", Codec = "h264", Width = 1280, Height = 720, Fps = 30, Publisher = "TAG1" }, Timeout());
        var announced = await started.Task.WaitAsync(Wait);
        Assert.Equal((streamId, publisher.MemberId, "tela", "TAG1"), (announced.Stream, announced.Owner, announced.Meta.Title, announced.Meta.Publisher));

        var watching = Signal<uint>();
        var wantsKeyframe = Signal<uint>();
        publisher.ViewerJoined += (stream, who) => { if (stream == streamId) watching.TrySetResult(who); };
        publisher.KeyframeRequested += stream => wantsKeyframe.TrySetResult(stream);
        viewer.Subscribe(streamId);
        Assert.Equal(viewer.MemberId, await watching.Task.WaitAsync(Wait));
        Assert.Equal(streamId, await wantsKeyframe.Task.WaitAsync(Wait));

        // A frame in two parts (header + bitstream), bigger than the client's first receive buffer.
        var head = new byte[VideoPacketHeader.Size];
        new VideoPacketHeader(1, 1234, 1280, 720, true).Write(head);
        var bitstream = new byte[600_000];
        Random.Shared.NextBytes(bitstream);
        var received = Signal<(MessageType Type, bool Key, byte[] Body, uint Stamp)>();
        viewer.MediaReceived += (stream, type, key, body, stamp) => { if (stream == streamId) received.TrySetResult((type, key, body, stamp)); };
        var frame = publisher.SealMedia(streamId, MessageType.Video, head, bitstream, keyframe: true, sendStamp: 77)!;
        publisher.SendSealedMedia(frame, streamId, isVideo: true, keyframe: true);
        var media = await received.Task.WaitAsync(Wait);
        Assert.Equal((MessageType.Video, true, 77u), (media.Type, media.Key, media.Stamp));
        Assert.True(VideoPacket.TryParse(media.Body, out var header, out var payload));
        Assert.Equal((1u, 1234L, 1280, 720, true), (header.Sequence, header.TimestampMs, header.Width, header.Height, header.IsKeyframe));
        Assert.True(payload.Span.SequenceEqual(bitstream));

        // The host stamps a relayed update with the sender's member id; the stream id rides inside.
        var updated = Signal<(uint Sender, StreamMetaMessage Meta)>();
        viewer.StreamMetaUpdated += (sender, meta) => updated.TrySetResult((sender, meta));
        publisher.UpdateStreamMeta(streamId, new StreamMetaMessage { Title = "tela", State = StreamStates.Paused });
        var update = await updated.Task.WaitAsync(Wait);
        Assert.Equal((publisher.MemberId, streamId, StreamStates.Paused), (update.Sender, update.Meta.StreamId, update.Meta.State));

        // Direct-link signals are relayed to the room and acted on only by the addressee.
        var forPublisher = Signal<(uint From, PeerSignal Signal)>();
        var leaked = false;
        publisher.SignalReceived += (from, signal) => forPublisher.TrySetResult((from, signal));
        viewer.SignalReceived += (_, _) => leaked = true;
        viewer.SendSignal(new PeerSignal { To = publisher.MemberId, Stream = streamId, Session = 9, Kind = PeerSignal.KindBye, Data = "test" });
        var signal = await forPublisher.Task.WaitAsync(Wait);
        Assert.Equal((viewer.MemberId, streamId, 9u, PeerSignal.KindBye, "test"), (signal.From, signal.Signal.Stream, signal.Signal.Session, signal.Signal.Kind, signal.Signal.Data));
        Assert.False(leaked);

        var ended = Signal<uint>();
        viewer.StreamEnded += stream => ended.TrySetResult(stream);
        publisher.Unpublish(streamId);
        Assert.Equal(streamId, await ended.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task RoomWithPassword_WrongIsRefused_RightJoinsAndReadsMedia()
    {
        using var publisher = await LoungeClient.CreateAsync(_host.Url, new RoomCreateOptions { Name = "locked", Password = "segredo" }, null, Timeout());

        var missing = await Assert.ThrowsAsync<LoungeException>(() => LoungeClient.JoinAsync(_host.Url, new RoomJoinOptions { Code = publisher.Code }, null, Timeout()));
        Assert.Equal(LoungeException.PasswordRequired, missing.Reason);
        var wrong = await Assert.ThrowsAsync<LoungeException>(() => LoungeClient.JoinAsync(_host.Url, new RoomJoinOptions { Code = publisher.Code, Password = "errada" }, null, Timeout()));
        Assert.Equal(LoungeProtocol.ReasonBadPassword, wrong.Reason);

        using var viewer = await LoungeClient.JoinAsync(_host.Url, new RoomJoinOptions { Code = publisher.Code, Password = "segredo" }, null, Timeout());
        Assert.Equal(publisher.ContentKey, viewer.ContentKey);

        var streamId = await publisher.PublishAsync(new StreamMetaMessage { Title = "som" }, Timeout());
        var watching = Signal<uint>();
        publisher.ViewerJoined += (_, who) => watching.TrySetResult(who);
        viewer.Subscribe(streamId);
        await watching.Task.WaitAsync(Wait);

        var audio = Signal<byte[]>();
        viewer.MediaReceived += (_, type, _, body, _) => { if (type == MessageType.Audio) audio.TrySetResult(body); };
        var packet = new byte[] { 1, 2, 3, 4, 5 };
        publisher.SendMedia(streamId, MessageType.Audio, packet, keyframe: false);
        Assert.Equal(packet, await audio.Task.WaitAsync(Wait));
    }
}
