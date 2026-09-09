using System.Diagnostics;

namespace Beamcast;

/// <summary>
/// Decides which captured frames are worth encoding. Desktop content that changed always goes
/// through. A frame where only the mouse moved goes through at most every
/// <see cref="CursorOnlyInterval"/> (and not at all when the cursor is not drawn), so a wiggling
/// mouse over a static screen does not cost a full encode per display refresh. With nothing
/// changing at all, the last frame is repeated once per <see cref="IdleRepeatInterval"/> so the
/// stream stays alive, or right away when someone asked for a keyframe.
/// Pure logic on Stopwatch ticks, so it can be tested without a display.
/// </summary>
public sealed class CapturePacer
{
    public static readonly TimeSpan CursorOnlyInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan IdleRepeatInterval = TimeSpan.FromSeconds(1);

    private long _lastDeliveredTicks;
    private long _lastCursorOnlyTicks;
    private int _requested;

    /// <summary>True when there is a frame to repeat (something was delivered since the last reset).</summary>
    public bool HasFrame => Volatile.Read(ref _lastDeliveredTicks) != 0;

    public void Reset()
    {
        _lastDeliveredTicks = 0;
        _lastCursorOnlyTicks = 0;
        Volatile.Write(ref _requested, 0);
    }

    /// <summary>A fresh capture arrived. <paramref name="contentChanged"/> is false when only the pointer moved.</summary>
    public bool ShouldDeliver(long nowTicks, bool contentChanged, bool cursorVisible)
    {
        if (contentChanged)
        {
            _lastDeliveredTicks = nowTicks;
            return true;
        }
        if (!cursorVisible)
            return false;
        if (_lastCursorOnlyTicks != 0 && Stopwatch.GetElapsedTime(_lastCursorOnlyTicks, nowTicks) < CursorOnlyInterval)
            return false;
        _lastCursorOnlyTicks = nowTicks;
        _lastDeliveredTicks = nowTicks;
        return true;
    }

    /// <summary>Someone needs a frame now (a viewer waiting for a keyframe); the next check repeats at once.</summary>
    public void RequestFrame() => Volatile.Write(ref _requested, 1);

    /// <summary>Periodic check: repeat the last frame now?</summary>
    public bool ShouldRepeat(long nowTicks)
    {
        if (_lastDeliveredTicks == 0)
            return false;
        if (Interlocked.Exchange(ref _requested, 0) == 1 || Stopwatch.GetElapsedTime(_lastDeliveredTicks, nowTicks) >= IdleRepeatInterval)
        {
            _lastDeliveredTicks = nowTicks;
            return true;
        }
        return false;
    }
}
