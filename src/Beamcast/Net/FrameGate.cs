namespace Beamcast.Net;

public enum GateDecision
{
    Send,
    Drop,
    DropAndRequestKeyframe,
}

/// <summary>
/// Per-viewer back-pressure policy. VP8 inter frames depend on the previous frame, so a viewer that
/// falls behind cannot simply skip a few frames: once anything is dropped the viewer must wait for
/// the next keyframe. The gate starts out waiting for a keyframe, which is also what a fresh viewer
/// needs before it can decode anything.
/// </summary>
public sealed class FrameGate
{
    private readonly int _maxPending;

    public FrameGate(int maxPending)
    {
        _maxPending = Math.Max(1, maxPending);
    }

    public bool AwaitingKeyframe { get; private set; } = true;

    public GateDecision Offer(bool isKeyframe, int pendingFrames)
    {
        if (pendingFrames >= _maxPending)
        {
            var wasWaiting = AwaitingKeyframe;
            AwaitingKeyframe = true;
            return wasWaiting ? GateDecision.Drop : GateDecision.DropAndRequestKeyframe;
        }

        if (AwaitingKeyframe)
        {
            if (!isKeyframe)
                return GateDecision.Drop;
            AwaitingKeyframe = false;
        }

        return GateDecision.Send;
    }

    /// <summary>Called when the viewer explicitly asks for a keyframe (e.g. after a decode error).</summary>
    public void RequestKeyframe() => AwaitingKeyframe = true;
}
