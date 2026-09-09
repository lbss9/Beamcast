using System.Diagnostics;
using Xunit;

namespace Beamcast.Tests;

public class CapturePacerTests
{
    private static long Ms(double ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void ContentChangesAlwaysGoThrough()
    {
        var pacer = new CapturePacer();
        var t = Ms(1000);
        Assert.True(pacer.ShouldDeliver(t, contentChanged: true, cursorVisible: true));
        Assert.True(pacer.ShouldDeliver(t + Ms(16), contentChanged: true, cursorVisible: true));
        Assert.True(pacer.ShouldDeliver(t + Ms(17), contentChanged: true, cursorVisible: false));
        Assert.True(pacer.HasFrame);
    }

    [Fact]
    public void MouseOnlyFramesArePacedToTenPerSecond()
    {
        var pacer = new CapturePacer();
        var t = Ms(1000);
        Assert.True(pacer.ShouldDeliver(t, contentChanged: false, cursorVisible: true), "first cursor move draws");
        Assert.False(pacer.ShouldDeliver(t + Ms(16), false, true));
        Assert.False(pacer.ShouldDeliver(t + Ms(99), false, true));
        Assert.True(pacer.ShouldDeliver(t + Ms(101), false, true));
        Assert.False(pacer.ShouldDeliver(t + Ms(150), false, true));
    }

    [Fact]
    public void MouseOnlyFramesAreDroppedWhenTheCursorIsHidden()
    {
        var pacer = new CapturePacer();
        Assert.False(pacer.ShouldDeliver(Ms(1000), contentChanged: false, cursorVisible: false));
        Assert.False(pacer.HasFrame);
    }

    [Fact]
    public void IdleRepeatsOncePerSecondAndNotBeforeAnythingWasDelivered()
    {
        var pacer = new CapturePacer();
        Assert.False(pacer.ShouldRepeat(Ms(5000)), "nothing to repeat yet");
        var t = Ms(1000);
        pacer.ShouldDeliver(t, true, true);
        Assert.False(pacer.ShouldRepeat(t + Ms(250)));
        Assert.False(pacer.ShouldRepeat(t + Ms(999)));
        Assert.True(pacer.ShouldRepeat(t + Ms(1001)));
        Assert.False(pacer.ShouldRepeat(t + Ms(1500)), "the repeat itself counts as a delivery");
        Assert.True(pacer.ShouldRepeat(t + Ms(2002)));
    }

    [Fact]
    public void KeyframeRequestRepeatsAtOnce()
    {
        var pacer = new CapturePacer();
        var t = Ms(1000);
        pacer.ShouldDeliver(t, true, true);
        pacer.RequestFrame();
        Assert.True(pacer.ShouldRepeat(t + Ms(100)));
        Assert.False(pacer.ShouldRepeat(t + Ms(200)), "the request is consumed");
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var pacer = new CapturePacer();
        pacer.ShouldDeliver(Ms(1000), true, true);
        pacer.RequestFrame();
        pacer.Reset();
        Assert.False(pacer.HasFrame);
        Assert.False(pacer.ShouldRepeat(Ms(9000)));
    }
}
