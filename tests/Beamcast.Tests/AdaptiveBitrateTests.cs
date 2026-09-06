using Beamcast.Net;
using Xunit;

namespace Beamcast.Tests;

public class AdaptiveBitrateTests
{
    [Fact]
    public void StartsAtTheCeiling()
    {
        var ladder = new AdaptiveBitrate();
        ladder.Reset(0);
        Assert.Equal(0, ladder.Level);
        Assert.False(ladder.IsAdapted);
        Assert.Equal(30000, ladder.Apply(30000));
    }

    [Fact]
    public void DropsStepDownAtMostOncePerWindow()
    {
        var ladder = new AdaptiveBitrate();
        ladder.Reset(0);
        Assert.False(ladder.OnDrop(100), "first drop right after reset is inside the step-down window");
        Assert.True(ladder.OnDrop(AdaptiveBitrate.StepDownAfterMs + 1));
        Assert.Equal(1, ladder.Level);
        Assert.False(ladder.OnDrop(AdaptiveBitrate.StepDownAfterMs + 500), "a burst of drops is one step");
        Assert.True(ladder.OnDrop(2 * AdaptiveBitrate.StepDownAfterMs + 10));
        Assert.Equal(2, ladder.Level);
        Assert.Equal((int)Math.Round(30000 * AdaptiveBitrate.Levels[2]), ladder.Apply(30000));
    }

    [Fact]
    public void NeverGoesBelowTheLastLevelOrTheFloor()
    {
        var ladder = new AdaptiveBitrate();
        ladder.Reset(0);
        var t = 0L;
        for (var i = 0; i < 20; i++)
        {
            t += AdaptiveBitrate.StepDownAfterMs + 1;
            ladder.OnDrop(t);
        }
        Assert.Equal(AdaptiveBitrate.Levels.Length - 1, ladder.Level);
        Assert.Equal(300, ladder.Apply(500));
    }

    [Fact]
    public void ClimbsBackOneLevelPerQuietSpell()
    {
        var ladder = new AdaptiveBitrate();
        ladder.Reset(0);
        var t = AdaptiveBitrate.StepDownAfterMs + 1;
        ladder.OnDrop(t);
        t += AdaptiveBitrate.StepDownAfterMs + 1;
        ladder.OnDrop(t);
        Assert.Equal(2, ladder.Level);

        Assert.False(ladder.OnTick(t + AdaptiveBitrate.StepUpAfterMs - 1), "not quiet for long enough yet");
        Assert.True(ladder.OnTick(t + AdaptiveBitrate.StepUpAfterMs + 1));
        Assert.Equal(1, ladder.Level);
        // A new drop restarts the quiet timer.
        var drop = t + AdaptiveBitrate.StepUpAfterMs + 2000;
        ladder.OnDrop(drop);
        Assert.False(ladder.OnTick(drop + AdaptiveBitrate.StepUpAfterMs - 1));
        Assert.True(ladder.OnTick(drop + AdaptiveBitrate.StepUpAfterMs + 1));
        Assert.True(ladder.OnTick(drop + 2 * AdaptiveBitrate.StepUpAfterMs + 5));
        Assert.Equal(0, ladder.Level);
        Assert.False(ladder.OnTick(drop + 3 * AdaptiveBitrate.StepUpAfterMs), "nothing above the ceiling");
    }

    [Fact]
    public void FoldedClockDeltaSurvivesTheWrap()
    {
        Assert.Equal(10, LoungeMux.ClockDelta(5, unchecked(5u - 10u)));
        Assert.Equal(-10, LoungeMux.ClockDelta(unchecked(5u - 10u), 5));
        Assert.Equal(0, LoungeMux.ClockDelta(123, 123));
    }
}
