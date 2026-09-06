namespace Beamcast;

/// <summary>
/// The adaptive-quality ladder. The bitrate the person chose is the ceiling; each level below it
/// is a fraction of that. Frames dropped at the uplink gate (the outbox could not keep up) push
/// the level down at most once per <see cref="StepDownAfterMs"/>; a quiet spell of
/// <see cref="StepUpAfterMs"/> without drops pulls it back up one level at a time.
/// Pure logic, fed with timestamps so it can be tested without a network.
/// </summary>
public sealed class AdaptiveBitrate
{
    /// <summary>Fraction of the chosen bitrate at each level; level 0 is the ceiling.</summary>
    public static readonly double[] Levels = [1.0, 0.66, 0.45, 0.30, 0.20];

    public const int StepDownAfterMs = 1500;
    public const int StepUpAfterMs = 10_000;

    private int _level;
    private long _lastChangeMs;
    private long _lastDropMs;

    public int Level => _level;
    public bool IsAdapted => _level > 0;

    /// <summary>The bitrate to run at right now for the chosen ceiling.</summary>
    public int Apply(int ceilingKbps) => Math.Max(300, (int)Math.Round(ceilingKbps * Levels[_level]));

    /// <summary>Back to the ceiling (a new broadcast starts clean).</summary>
    public void Reset(long nowMs)
    {
        _level = 0;
        _lastChangeMs = nowMs;
        _lastDropMs = 0;
    }

    /// <summary>The uplink gate dropped a frame. Returns true when the level changed.</summary>
    public bool OnDrop(long nowMs)
    {
        _lastDropMs = nowMs;
        if (_level >= Levels.Length - 1 || nowMs - _lastChangeMs < StepDownAfterMs)
            return false;
        _level++;
        _lastChangeMs = nowMs;
        return true;
    }

    /// <summary>Periodic check. Returns true when the level changed (went up).</summary>
    public bool OnTick(long nowMs)
    {
        if (_level == 0)
            return false;
        var quietSince = Math.Max(_lastDropMs, _lastChangeMs);
        if (nowMs - quietSince < StepUpAfterMs)
            return false;
        _level--;
        _lastChangeMs = nowMs;
        return true;
    }
}
