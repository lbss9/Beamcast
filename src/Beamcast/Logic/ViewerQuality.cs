namespace Beamcast;

/// <summary>
/// Which version of the stream one viewer on a direct link gets: the full one, or the reduced one
/// (smaller picture, a quarter of the bitrate, half the frame rate above 30 fps) that a second
/// encoder produces only while somebody needs it.
///
/// Two complaints in a few seconds (a late picture, or the link holding frames back) move the
/// viewer down. After a quiet spell it is tried on the full stream again; if it struggles right
/// away it goes back down at the first complaint and stays there longer each time, so a
/// connection that simply cannot carry the full stream is not bounced up and down for ever.
/// Pure logic, fed with timestamps so it can be tested without a network.
/// </summary>
public sealed class ViewerQuality
{
    public const int ComplaintsToReduce = 2;
    public const int WindowMs = 6000;

    /// <summary>A complaint this soon after going back to full sends the viewer down at once.</summary>
    public const int ProbationMs = 15_000;

    /// <summary>Quiet time before trying the full stream again, per time it has been reduced.</summary>
    public static readonly int[] HoldSeconds = [30, 60, 120, 300];

    private readonly List<long> _complaints = [];
    private long _quietSince;
    private long _restoredAt = long.MinValue / 2;
    private int _stints;

    public bool Reduced { get; private set; }

    /// <summary>The viewer is struggling. True when it should move to the reduced stream now.</summary>
    public bool Complain(long nowMs)
    {
        if (Reduced)
        {
            _quietSince = nowMs; // still struggling: the wait for the full stream starts over
            return false;
        }
        _complaints.RemoveAll(at => nowMs - at > WindowMs);
        _complaints.Add(nowMs);
        var needed = nowMs - _restoredAt < ProbationMs ? 1 : ComplaintsToReduce;
        if (_complaints.Count < needed)
            return false;
        Reduced = true;
        _stints++;
        _quietSince = nowMs;
        _complaints.Clear();
        return true;
    }

    /// <summary>Periodic check. True when the viewer should be tried on the full stream again now.</summary>
    public bool Tick(long nowMs)
    {
        if (!Reduced || nowMs - _quietSince < HoldMs)
            return false;
        Reduced = false;
        _restoredAt = nowMs;
        return true;
    }

    /// <summary>How long the current (or next) reduced spell lasts without complaints.</summary>
    public long HoldMs => HoldSeconds[Math.Clamp(_stints - 1, 0, HoldSeconds.Length - 1)] * 1000L;

    /// <summary>The reduced stream is gone (encoder failed, link closed): back to full without probation.</summary>
    public void ForceFull()
    {
        Reduced = false;
        _complaints.Clear();
    }
}
