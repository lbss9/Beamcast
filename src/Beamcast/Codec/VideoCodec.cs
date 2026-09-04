namespace Beamcast.Codec;

/// <summary>Codecs the wire protocol can carry. Names are the values sent in the Welcome message.</summary>
public enum VideoCodec
{
    Vp8,
    H264,
    Hevc,
}

public static class VideoCodecs
{
    public const string Vp8Name = "vp8";
    public const string H264Name = "h264";
    public const string HevcName = "hevc";

    public static string ToWireName(this VideoCodec codec) =>
        codec switch
        {
            VideoCodec.H264 => H264Name,
            VideoCodec.Hevc => HevcName,
            _ => Vp8Name,
        };

    public static bool TryParse(string? name, out VideoCodec codec)
    {
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case H264Name:
            case "avc":
                codec = VideoCodec.H264;
                return true;
            case HevcName:
            case "h265":
                codec = VideoCodec.Hevc;
                return true;
            case Vp8Name:
                codec = VideoCodec.Vp8;
                return true;
            default:
                codec = VideoCodec.Vp8;
                return false;
        }
    }

    public static bool IsGpu(this VideoCodec codec) => codec != VideoCodec.Vp8;
}

/// <summary>What the person asked for in settings; resolved to a <see cref="VideoCodec"/> at go-live.</summary>
public static class EncoderPreference
{
    public const string Auto = "Auto";
    public const string H264 = "H264";
    public const string Hevc = "HEVC";
    public const string Vp8 = "VP8";

    public static readonly string[] All = [Auto, H264, Hevc, Vp8];

    public static string Normalize(string? value) =>
        All.FirstOrDefault(v => string.Equals(v, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Auto;
}
