namespace Beamcast;

/// <summary>Output resolution ceilings the encoder can be asked to respect.</summary>
public static class QualityPreset
{
    public const string Source = "Source";
    public const string P1080 = "1080p";
    public const string P720 = "720p";
    public const string P480 = "480p";

    public static readonly string[] All = [Source, P1080, P720, P480];

    public static readonly int[] FpsOptions = [15, 24, 30, 60];

    public const int MinBitrateKbps = 300;
    public const int MaxBitrateKbps = 50_000;

    public static string Normalize(string? preset) =>
        All.FirstOrDefault(p => string.Equals(p, preset?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? P1080;

    public static int NormalizeFps(int fps) => FpsOptions.Contains(fps) ? fps : 30;

    public static int ClampBitrate(int kbps) => Math.Clamp(kbps, MinBitrateKbps, MaxBitrateKbps);

    /// <summary>Maximum output height for the preset, or 0 for "keep the source size".</summary>
    public static int MaxHeight(string preset) =>
        Normalize(preset) switch
        {
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

    /// <summary>A sensible default bitrate for the preset and frame rate, in kbps.</summary>
    public static int SuggestedBitrate(string preset, int fps)
    {
        var baseline = Normalize(preset) switch
        {
            P480 => 1200,
            P720 => 2500,
            P1080 => 5000,
            _ => 8000,
        };
        return fps >= 60 ? (int)(baseline * 1.5) : baseline;
    }
}
