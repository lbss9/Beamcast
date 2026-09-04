namespace Beamcast;

/// <summary>Output resolution ceilings the encoder can be asked to respect.</summary>
public static class QualityPreset
{
    public const string Source = "Source";
    public const string P2160 = "2160p";
    public const string P1440 = "1440p";
    public const string P1080 = "1080p";
    public const string P720 = "720p";
    public const string P480 = "480p";

    public static readonly string[] All = [Source, P2160, P1440, P1080, P720, P480];

    public static readonly int[] FpsOptions = [15, 24, 30, 60, 120];

    public const int MinBitrateKbps = 300;
    public const int MaxBitrateKbps = 150_000;

    public static string Normalize(string? preset) =>
        All.FirstOrDefault(p => string.Equals(p, preset?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? P1080;

    public static int NormalizeFps(int fps) => FpsOptions.Contains(fps) ? fps : 30;

    public static int ClampBitrate(int kbps) => Math.Clamp(kbps, MinBitrateKbps, MaxBitrateKbps);

    /// <summary>Maximum output height for the preset, or 0 for "keep the source size".</summary>
    public static int MaxHeight(string preset) =>
        Normalize(preset) switch
        {
            P2160 => 2160,
            P1440 => 1440,
            P1080 => 1080,
            P720 => 720,
            P480 => 480,
            _ => 0,
        };

    /// <summary>
    /// Computes the encoded size for a source of the given dimensions.
    /// Never upscales, keeps the aspect ratio and returns even dimensions (needed for 4:2:0 chroma).
    /// </summary>
    public static (int Width, int Height) Fit(string preset, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (0, 0);

        var maxHeight = MaxHeight(preset);
        if (maxHeight <= 0 || height <= maxHeight)
            return (Even(width), Even(height));

        var scale = maxHeight / (double)height;
        var outWidth = (int)Math.Round(width * scale);
        return (Even(Math.Max(2, outWidth)), Even(maxHeight));
    }

    private static int Even(int value) => value < 2 ? 2 : value & ~1;

    /// <summary>
    /// A sensible default bitrate in kbps. GPU codecs get streaming-grade numbers (H.264 at 4K60
    /// wants ~40 Mbps to look clean); HEVC needs roughly a third less; VP8 on the CPU stays modest.
    /// </summary>
    public static int SuggestedBitrate(string preset, int fps, string codec = "h264")
    {
        var baseline = Normalize(preset) switch
        {
            P480 => 1500,
            P720 => 4000,
            P1080 => 8000,
            P1440 => 16000,
            P2160 => 30000,
            _ => 30000,
        };

        var kbps = fps >= 120 ? baseline * 2.0 : fps >= 60 ? baseline * 1.4 : baseline;
        kbps *= codec.ToLowerInvariant() switch
        {
            "hevc" => 0.65,
            "vp8" => 0.6,
            _ => 1.0,
        };
        return ClampBitrate((int)Math.Round(kbps / 250.0) * 250);
    }
}
