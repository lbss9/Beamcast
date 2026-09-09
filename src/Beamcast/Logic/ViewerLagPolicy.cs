namespace Beamcast;

/// <summary>
/// What a viewer does about its own delay. Evaluated once a second with the measured
/// glass-to-glass delay (negative = unknown). Above <see cref="ReportAboveMs"/> it tells the
/// publisher every <see cref="ReportEveryMs"/>, so the publisher can lower its bitrate from real
/// numbers instead of guessing. Above <see cref="KeyframeAboveMs"/> it also asks the host to drop
/// what is queued for it and send a fresh keyframe, every <see cref="KeyframeEveryMs"/> at most.
/// Pure logic, tested without a network.
/// </summary>
public sealed class ViewerLagPolicy
{
    public const int ReportAboveMs = 400;
    public const int KeyframeAboveMs = 900;
    public const int ReportEveryMs = 2000;
    public const int KeyframeEveryMs = 3000;

    private long _lastReportMs = long.MinValue / 2;
    private long _lastKeyframeMs = long.MinValue / 2;

    [Flags]
    public enum Steps
    {
        None = 0,
        Report = 1,
        Keyframe = 2,
    }

    public Steps Evaluate(long nowMs, double latencyMs)
    {
        if (latencyMs < ReportAboveMs)
            return Steps.None;
        var action = Steps.None;
        if (nowMs - _lastReportMs >= ReportEveryMs)
        {
            _lastReportMs = nowMs;
            action |= Steps.Report;
        }
        if (latencyMs >= KeyframeAboveMs && nowMs - _lastKeyframeMs >= KeyframeEveryMs)
        {
            _lastKeyframeMs = nowMs;
            action |= Steps.Keyframe;
        }
        return action;
    }
}
