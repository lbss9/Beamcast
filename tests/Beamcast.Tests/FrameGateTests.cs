using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class FrameGateTests
{
    [Fact]
    public void FreshViewerOnlyAcceptsAKeyframeFirst()
    {
        var gate = new FrameGate(4);

        Assert.Equal(GateDecision.Drop, gate.Offer(isKeyframe: false, pendingFrames: 0));
        Assert.True(gate.AwaitingKeyframe);
        Assert.Equal(GateDecision.Send, gate.Offer(isKeyframe: true, pendingFrames: 0));
        Assert.False(gate.AwaitingKeyframe);
        Assert.Equal(GateDecision.Send, gate.Offer(isKeyframe: false, pendingFrames: 1));
    }

    [Fact]
    public void OverflowDropsAndAsksForOneKeyframe()
    {
        var gate = new FrameGate(2);
        gate.Offer(true, 0);

        Assert.Equal(GateDecision.DropAndRequestKeyframe, gate.Offer(false, 2));
        Assert.Equal(GateDecision.Drop, gate.Offer(false, 2));
        Assert.Equal(GateDecision.Drop, gate.Offer(false, 0));
        Assert.Equal(GateDecision.Send, gate.Offer(true, 0));
    }

    [Fact]
    public void ExplicitRequestReturnsToWaiting()
    {
        var gate = new FrameGate(3);
        gate.Offer(true, 0);
        gate.RequestKeyframe();

        Assert.Equal(GateDecision.Drop, gate.Offer(false, 0));
        Assert.Equal(GateDecision.Send, gate.Offer(true, 0));
    }
}
