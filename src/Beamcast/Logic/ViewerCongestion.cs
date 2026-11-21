namespace Beamcast;

/// <summary>
/// Decides whether viewers' trouble is a reason to lower the bitrate for everyone. One encoder
/// feeds every viewer, so lowering it for a single person on a bad connection makes the picture
/// worse for all the others, who were fine. The bitrate only follows the viewers when at least
/// half of them complained recently (a late picture, or a direct link that held frames back);
/// a lone struggler keeps the full stream and simply skips to the next keyframe.
/// Pure logic, fed with timestamps so it can be tested without a network.
/// </summary>
public sealed class ViewerCongestion
{
    /// <summary>How long a complaint counts.</summary>
    public const int WindowMs = 5000;

    private readonly Dictionary<uint, long> _lastComplaint = new();

    /// <summary>
    /// Records that <paramref name="viewerId"/> is struggling and says whether enough viewers are,
    /// out of <paramref name="viewerCount"/> watching, to lower the bitrate.
    /// </summary>
    public bool Complain(uint viewerId, long nowMs, int viewerCount)
    {
        lock (_lastComplaint)
        {
            _lastComplaint[viewerId] = nowMs;
            foreach (var stale in _lastComplaint.Where(p => nowMs - p.Value > WindowMs).Select(p => p.Key).ToList())
                _lastComplaint.Remove(stale);
            return _lastComplaint.Count >= Needed(viewerCount);
        }
    }

    /// <summary>Complaints needed among that many viewers: half of them, rounded up, and never less than one.</summary>
    public static int Needed(int viewerCount) => Math.Max(1, (viewerCount + 1) / 2);

    public void Forget(uint viewerId)
    {
        lock (_lastComplaint)
            _lastComplaint.Remove(viewerId);
    }

    public void Reset()
    {
        lock (_lastComplaint)
            _lastComplaint.Clear();
    }
}
