namespace Beamcast.Capture;

public enum CaptureSourceKind
{
    Monitor,
    Window,
}

/// <summary>A monitor or top-level window the user can pick to share.</summary>
public sealed record CaptureSource(
    CaptureSourceKind Kind,
    IntPtr Handle,
    string Title,
    string Subtitle,
    int Width,
    int Height,
    bool IsPrimary
)
{
    public string Key => $"{Kind}:{Handle}";

    public string SizeLabel => Width > 0 && Height > 0 ? $"{Width}×{Height}" : string.Empty;
}
