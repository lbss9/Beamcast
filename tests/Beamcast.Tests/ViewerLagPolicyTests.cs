using Xunit;
using static Beamcast.ViewerLagPolicy;

namespace Beamcast.Tests;

public class ViewerLagPolicyTests
{
    [Fact]
    public void LowOrUnknownDelayDoesNothing()
    {
        var policy = new ViewerLagPolicy();
        Assert.Equal(Steps.None, policy.Evaluate(1000, -1));
        Assert.Equal(Steps.None, policy.Evaluate(2000, 120));
        Assert.Equal(Steps.None, policy.Evaluate(3000, ReportAboveMs - 1));
    }

    [Fact]
    public void HighDelayReportsEveryTwoSeconds()
    {
        var policy = new ViewerLagPolicy();
        Assert.Equal(Steps.Report, policy.Evaluate(1000, 500));
        Assert.Equal(Steps.None, policy.Evaluate(2000, 500));
        Assert.Equal(Steps.Report, policy.Evaluate(3001, 500));
    }

    [Fact]
    public void VeryHighDelayAlsoAsksForAKeyframe()
    {
        var policy = new ViewerLagPolicy();
        Assert.Equal(Steps.Report | Steps.Keyframe, policy.Evaluate(1000, 1500));
        Assert.Equal(Steps.None, policy.Evaluate(2000, 1500));
        Assert.Equal(Steps.Report, policy.Evaluate(3001, 1500));
        Assert.Equal(Steps.Keyframe, policy.Evaluate(4001, 1500));
    }

    [Fact]
    public void RecoveringStopsBothWithoutResettingCooldowns()
    {
        var policy = new ViewerLagPolicy();
        policy.Evaluate(1000, 1500);
        Assert.Equal(Steps.None, policy.Evaluate(2000, 100));
        Assert.Equal(Steps.Report, policy.Evaluate(3001, 600));
    }
}
