namespace Beamcast.Net;

/// <summary>
/// Counts failed join attempts (wrong password, bad invite, unknown room) per key and blocks a key
/// once it fails too often inside a sliding window. Pure logic; the server keeps one per scope
/// (per client address and per room) so neither a single guesser nor a distributed one gets far.
/// </summary>
public sealed class JoinRateLimiter
{
    private readonly Dictionary<string, List<long>> _failures = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly int _maxFailures;
    private readonly long _windowMs;
    private long _lastPrune;

    public JoinRateLimiter(int maxFailures, TimeSpan window)
    {
        _maxFailures = Math.Max(1, maxFailures);
        _windowMs = (long)window.TotalMilliseconds;
    }

    public bool IsBlocked(string key, long nowMs)
    {
        lock (_sync)
        {
            Prune(nowMs);
            return _failures.TryGetValue(key, out var list) && Count(list, nowMs) >= _maxFailures;
        }
    }

    public void RecordFailure(string key, long nowMs)
    {
        lock (_sync)
        {
            if (!_failures.TryGetValue(key, out var list))
                _failures[key] = list = [];
            list.Add(nowMs);
            if (list.Count > _maxFailures * 4)
                list.RemoveRange(0, list.Count - _maxFailures * 4);
        }
    }

    public void Clear(string key)
    {
        lock (_sync)
            _failures.Remove(key);
    }

    private int Count(List<long> list, long nowMs) => list.Count(t => nowMs - t < _windowMs);

    private void Prune(long nowMs)
    {
        if (nowMs - _lastPrune < _windowMs)
            return;
        _lastPrune = nowMs;
        foreach (var key in _failures.Where(kv => Count(kv.Value, nowMs) == 0).Select(kv => kv.Key).ToList())
            _failures.Remove(key);
    }
}
