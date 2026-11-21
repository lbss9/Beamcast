using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class DirectLinkTests
{
    [Fact]
    public void QueuedFramesBehindANewerKeyframeAreStale()
    {
        Assert.False(OutboxPolicy.IsStale(order: 5, newestKeyframeOrder: 0));
        Assert.False(OutboxPolicy.IsStale(order: 5, newestKeyframeOrder: 5)); // the keyframe itself
        Assert.False(OutboxPolicy.IsStale(order: 7, newestKeyframeOrder: 5)); // frames after it build on it
        Assert.True(OutboxPolicy.IsStale(order: 4, newestKeyframeOrder: 5));
    }

    [Fact]
    public void SignalsAndStreamIdsRoundTripThroughJson()
    {
        var signal = new PeerSignal { To = 7, Stream = 3, Session = 99, Kind = PeerSignal.KindIce, Data = "{\"candidate\":\"x\"}" };
        var parsed = Json.Deserialize<PeerSignal>(Json.Serialize(signal))!;
        Assert.Equal((7u, 3u, 99u, PeerSignal.KindIce, signal.Data), (parsed.To, parsed.Stream, parsed.Session, parsed.Kind, parsed.Data));

        var meta = Json.Deserialize<StreamMetaMessage>(Json.Serialize(new StreamMetaMessage { Title = "a", StreamId = 12 }))!;
        Assert.Equal(12u, meta.StreamId);

        // Messages from apps before 3.1 have neither field.
        Assert.False(Json.Deserialize<PresenceMessage>("{\"name\":\"x\"}")!.Direct);
        Assert.Equal(0u, Json.Deserialize<StreamMetaMessage>("{\"title\":\"x\"}")!.StreamId);
    }

    /// <summary>Two real links on this machine, signals passed by hand: opens, carries a frame bigger than one chunk, closes both ends.</summary>
    [Fact]
    public async Task TwoLinksConnectAndCarryALargeFrame()
    {
        PeerLink? viewer = null;
        var viewerReady = new TaskCompletionSource<PeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisherOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerClosed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var toViewer = new List<PeerSignal>();

        PeerLink publisher = null!;
        publisher = new PeerLink(peerId: 2, streamId: 5, session: 1, offerer: true, signal: s =>
        {
            // Deliver in order, creating the viewer's link on the offer, as LoungeService does.
            lock (toViewer)
            {
                if (s.Kind == PeerSignal.KindOffer && viewer is null)
                {
                    viewer = new PeerLink(peerId: 1, streamId: 5, session: s.Session, offerer: false, signal: back => _ = Deliver(viewerReady, back));
                    viewer.Received += (_, frame) => received.TrySetResult(frame);
                    viewer.Closed += (_, reason) => viewerClosed.TrySetResult(reason);
                    viewerReady.TrySetResult(viewer);
                }
                viewer?.HandleAsync(s).GetAwaiter().GetResult();
            }
        });
        publisher.Opened += _ => publisherOpen.TrySetResult();

        async Task Deliver(TaskCompletionSource<PeerLink> _, PeerSignal back) => await publisher.HandleAsync(back);

        await publisher.StartAsync();
        await publisherOpen.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(publisher.IsOpen);

        var frame = new byte[PeerLink.ChunkSize * 3 + 1234];
        Random.Shared.NextBytes(frame);
        // The viewer's side may report open a moment after the publisher's.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!publisher.Send(frame) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(frame, got);

        publisher.Close("local:stopped", notifyPeer: true);
        var reason = await viewerClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("bye:", reason);
        viewer?.Dispose();
        publisher.Dispose();
    }
}
