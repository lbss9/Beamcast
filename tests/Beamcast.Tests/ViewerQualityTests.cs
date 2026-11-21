using Xunit;

namespace Beamcast.Tests;

public class ViewerQualityTests
{
    [Fact]
    public void TwoComplaintsCloseTogetherReduceOneAloneDoesNot()
    {
        var quality = new ViewerQuality();
        Assert.False(quality.Complain(1000));
        Assert.False(quality.Reduced);
        // The first one is forgotten after the window.
        Assert.False(quality.Complain(1000 + ViewerQuality.WindowMs + 1));
        Assert.True(quality.Complain(1000 + ViewerQuality.WindowMs + 2000));
        Assert.True(quality.Reduced);
    }

    [Fact]
    public void GoesBackToFullAfterAQuietSpellAndComplaintsRestartTheWait()
    {
        var quality = new ViewerQuality();
        quality.Complain(0);
        Assert.True(quality.Complain(1000));
        var hold = quality.HoldMs;
        Assert.Equal(ViewerQuality.HoldSeconds[0] * 1000L, hold);

        Assert.False(quality.Tick(1000 + hold - 1));
        Assert.False(quality.Complain(20_000)); // still struggling on the reduced stream
        Assert.False(quality.Tick(1000 + hold));  // the wait started over at 20 s
        Assert.True(quality.Tick(20_000 + hold));
        Assert.False(quality.Reduced);
    }

    [Fact]
    public void StrugglingRightAfterGoingBackReducesAtOnceAndForLonger()
    {
        var quality = new ViewerQuality();
        quality.Complain(0);
        quality.Complain(1000);
        var firstHold = quality.HoldMs;
        var restored = 1000 + firstHold;
        Assert.True(quality.Tick(restored));

        // Within probation one complaint is enough.
        Assert.True(quality.Complain(restored + ViewerQuality.ProbationMs - 1));
        Assert.True(quality.HoldMs > firstHold);

        // After probation it takes two again.
        var again = restored + ViewerQuality.ProbationMs - 1 + quality.HoldMs;
        Assert.True(quality.Tick(again));
        Assert.False(quality.Complain(again + ViewerQuality.ProbationMs + 1));
    }

    [Fact]
    public void TheHoldStopsGrowingAtTheLastStep()
    {
        var quality = new ViewerQuality();
        long now = 0;
        for (var i = 0; i < ViewerQuality.HoldSeconds.Length + 3; i++)
        {
            quality.Complain(now);
            quality.Complain(now + 1); // within probation the first already reduced; harmless
            Assert.True(quality.Reduced);
            now += quality.HoldMs + 10;
            Assert.True(quality.Tick(now));
        }
        quality.Complain(now);
        Assert.Equal(ViewerQuality.HoldSeconds[^1] * 1000L, quality.HoldMs);
    }

    [Fact]
    public void ForceFullLeavesNoProbation()
    {
        var quality = new ViewerQuality();
        quality.Complain(0);
        quality.Complain(1);
        quality.ForceFull();
        Assert.False(quality.Reduced);
        Assert.False(quality.Complain(100)); // one complaint is not enough again
    }

    [Theory]
    [InlineData(1920, 1080, 960, 540)]
    [InlineData(2560, 1440, 1280, 720)]
    [InlineData(3840, 2160, 1280, 720)]   // capped at 720 lines
    [InlineData(1280, 720, 640, 360)]
    [InlineData(854, 480, 640, 360)]      // never below 360 lines
    [InlineData(640, 360, 640, 360)]      // already small: same size, lower bitrate only
    [InlineData(2560, 1080, 1280, 540)]   // ultrawide keeps its shape
    public void ReducedSizeHalvesWithinBounds(int width, int height, int reducedWidth, int reducedHeight) =>
        Assert.Equal((reducedWidth, reducedHeight), QualityPreset.ReducedSize(width, height));

    [Fact]
    public void ReducedBitrateAndFps()
    {
        Assert.Equal(3000, QualityPreset.ReducedBitrate(12_000));
        Assert.Equal(500, QualityPreset.ReducedBitrate(1200));
        Assert.Equal(300, QualityPreset.ReducedBitrate(300)); // never above the full one
        Assert.Equal(30, QualityPreset.ReducedFps(60));
        Assert.Equal(60, QualityPreset.ReducedFps(120));
        Assert.Equal(30, QualityPreset.ReducedFps(30));
        Assert.Equal((0, 0), QualityPreset.ReducedSize(0, 0));
    }
}
