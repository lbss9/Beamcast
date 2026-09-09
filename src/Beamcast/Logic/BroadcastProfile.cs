using Beamcast.Codec;

namespace Beamcast;

public enum BroadcastProfile
{
    Manual,
    /// <summary>Measure, then pick Performance, Balanced or Quality from what this machine and connection sustain.</summary>
    Recommended,
    Performance,
    Balanced,
    Quality,
}

public static class BroadcastProfileNames
{
    public const string Manual = "Manual";
    public const string Recommended = "Recommended";
    public const string Performance = "Performance";
    public const string Balanced = "Balanced";
    public const string Quality = "Quality";

    public static BroadcastProfile Parse(string? value) => value switch
    {
        Recommended => BroadcastProfile.Recommended,
        Performance => BroadcastProfile.Performance,
        Balanced => BroadcastProfile.Balanced,
        Quality => BroadcastProfile.Quality,
        _ => BroadcastProfile.Manual,
    };

    public static string ToName(BroadcastProfile profile) => profile switch
    {
        BroadcastProfile.Recommended => Recommended,
        BroadcastProfile.Performance => Performance,
        BroadcastProfile.Balanced => Balanced,
        BroadcastProfile.Quality => Quality,
        _ => Manual,
    };
}

/// <summary>One measured preset: how long the hardware encoder took per frame at that size, and how many frames per second it kept up with when fed at up to 60.</summary>
public sealed record EncodeSample(string Preset, int Width, int Height, double EncodeMs, double EncodedFps);

/// <summary>Everything the recommendation is computed from. <paramref name="UplinkKbps"/> = 0 when not measured.</summary>
public sealed record ProfileInputs(int SourceWidth, int SourceHeight, IReadOnlyList<EncodeSample> Samples, int UplinkKbps, bool HasH264, bool HasHevc);

/// <summary>What a profile settles on, with the numbers that justified it. <paramref name="Tier"/> is the tier actually applied (Recommended resolves to one of the three).</summary>
public sealed record ProfileChoice(string Preset, int Fps, int BitrateKbps, string Encoder, int Width, int Height, double EncodeMs, int UplinkKbps, BroadcastProfile Tier);

/// <summary>
/// Turns measurements into settings. The rules are deliberately simple and written down here:
/// a candidate (preset, fps) "fits" when the measured encode time is under a fraction of the
/// frame budget (1000/fps ms), the fraction being the profile's appetite. Bitrate starts from the
/// codec's suggested value for the size and rate, is scaled by the profile, and is capped by a
/// share of the measured uplink when one exists.
/// </summary>
public static class BroadcastProfiles
{
    /// <summary>Presets worth measuring for a source of this size, largest first. Never upscales.</summary>
    public static IReadOnlyList<string> Candidates(int sourceWidth, int sourceHeight)
    {
        var list = new List<string>();
        foreach (var preset in new[] { QualityPreset.P2160, QualityPreset.P1440, QualityPreset.P1080, QualityPreset.P720 })
        {
            if (QualityPreset.MaxHeight(preset) <= sourceHeight)
                list.Add(preset);
        }
        // A source smaller than the smallest ladder step (or an odd size) is measured as itself.
        if (list.Count == 0 || sourceHeight < QualityPreset.MaxHeight(QualityPreset.P720))
            list.Insert(0, QualityPreset.Source);
        return list;
    }

    public static double BudgetFraction(BroadcastProfile profile) => profile switch
    {
        BroadcastProfile.Performance => 0.35,
        BroadcastProfile.Balanced => 0.55,
        _ => 0.75,
    };

    public static double BitrateScale(BroadcastProfile profile) => profile switch
    {
        BroadcastProfile.Performance => 0.75,
        BroadcastProfile.Balanced => 1.0,
        _ => 1.25,
    };

    public static double UplinkShare(BroadcastProfile profile) => profile switch
    {
        BroadcastProfile.Performance => 0.5,
        BroadcastProfile.Balanced => 0.6,
        _ => 0.7,
    };

    public static bool Fits(EncodeSample sample, int fps, BroadcastProfile profile) =>
        sample.EncodeMs > 0
        && sample.EncodeMs <= 1000.0 / fps * BudgetFraction(profile)
        && sample.EncodedFps >= Math.Min(fps, 60) * 0.9;

    public static ProfileChoice? Recommend(BroadcastProfile profile, ProfileInputs inputs)
    {
        if (profile == BroadcastProfile.Manual || inputs.Samples.Count == 0)
            return null;
        if (profile == BroadcastProfile.Recommended)
            return Recommend(PickTier(inputs), inputs);

        var encoder = profile == BroadcastProfile.Quality && inputs.HasHevc ? EncoderPreference.Hevc
            : inputs.HasH264 ? EncoderPreference.H264
            : inputs.HasHevc ? EncoderPreference.Hevc
            : EncoderPreference.Vp8;
        var codec = encoder == EncoderPreference.Hevc ? "hevc" : encoder == EncoderPreference.Vp8 ? "vp8" : "h264";

        // Samples largest first; the ladder below picks size and rate per profile.
        var ordered = inputs.Samples.OrderByDescending(s => s.Height).ToList();
        EncodeSample? pick = null;
        var fps = 30;
        switch (profile)
        {
            case BroadcastProfile.Performance:
                // The smallest size that still fits at 30; prefer 720p, never above 1080p.
                pick = ordered.Where(s => s.Height <= 720 && Fits(s, 30, profile)).OrderByDescending(s => s.Height).FirstOrDefault()
                    ?? ordered.Where(s => s.Height <= 1080 && Fits(s, 30, profile)).OrderBy(s => s.Height).FirstOrDefault()
                    ?? ordered.OrderBy(s => s.Height).First();
                fps = 30;
                break;
            case BroadcastProfile.Balanced:
                // 1080p (or the source when smaller) at 30, stepping down while the uplink cannot
                // carry it; 60 only when the encoder clearly fits and the uplink carries that too.
                pick = ordered.Where(s => s.Height <= 1080 && Fits(s, 30, profile) && UplinkAllows(inputs, s, 30, codec, profile)).OrderByDescending(s => s.Height).FirstOrDefault()
                    ?? ordered.Where(s => Fits(s, 30, profile) && UplinkAllows(inputs, s, 30, codec, profile)).OrderByDescending(s => s.Height).FirstOrDefault()
                    ?? ordered.Where(s => Fits(s, 30, profile)).OrderBy(s => s.Height).FirstOrDefault()
                    ?? ordered.OrderBy(s => s.Height).First();
                fps = Fits(pick, 60, profile) && UplinkAllows(inputs, pick, 60, codec, profile) ? 60 : 30;
                break;
            case BroadcastProfile.Quality:
                // Largest first: 60 when encoder and uplink allow, else 30, else the next size down.
                foreach (var s in ordered)
                {
                    if (Fits(s, 60, profile) && UplinkAllows(inputs, s, 60, codec, profile)) { pick = s; fps = 60; break; }
                    if (Fits(s, 30, profile) && UplinkAllows(inputs, s, 30, codec, profile)) { pick = s; fps = 30; break; }
                }
                if (pick is null)
                {
                    pick = ordered.FirstOrDefault(s => Fits(s, 30, profile)) ?? ordered.OrderBy(s => s.Height).First();
                    fps = 30;
                }
                break;
        }
        if (encoder == EncoderPreference.Vp8)
        {
            // CPU encoder: keep it small and slow regardless of what the GPU samples said.
            pick = ordered.Where(s => s.Height <= 720).OrderByDescending(s => s.Height).FirstOrDefault() ?? ordered.OrderBy(s => s.Height).First();
            fps = 30;
        }

        var kbps = QualityPreset.SuggestedBitrate(pick!.Preset == QualityPreset.Source ? PresetForHeight(pick.Height) : pick.Preset, fps, codec) * BitrateScale(profile);
        if (inputs.UplinkKbps > 0)
            kbps = Math.Min(kbps, inputs.UplinkKbps * UplinkShare(profile));
        var bitrate = QualityPreset.ClampBitrate((int)Math.Round(Math.Max(1000, kbps) / 250.0) * 250);
        return new ProfileChoice(pick.Preset, fps, bitrate, encoder, pick.Width, pick.Height, pick.EncodeMs, inputs.UplinkKbps, profile);
    }

    /// <summary>
    /// The tier this machine earns. Quality when the GPU encodes at least 1080p at 60 within the
    /// Quality budget and the uplink (when measured) carries that bitrate without capping it;
    /// Balanced when 1080p fits within the Balanced budget and the uplink carries it;
    /// Performance otherwise, and always without a hardware encoder.
    /// </summary>
    public static BroadcastProfile PickTier(ProfileInputs inputs)
    {
        if (!inputs.HasH264 && !inputs.HasHevc)
            return BroadcastProfile.Performance;
        var quality = Recommend(BroadcastProfile.Quality, inputs);
        if (quality is not null && (quality is { Height: >= 1080, Fps: 60 } || quality.Height >= 1440) && !UplinkCapped(inputs, quality, BroadcastProfile.Quality))
            return BroadcastProfile.Quality;
        var balanced = Recommend(BroadcastProfile.Balanced, inputs);
        if (balanced is { Height: >= 1080 } && !UplinkCapped(inputs, balanced, BroadcastProfile.Balanced))
            return BroadcastProfile.Balanced;
        return BroadcastProfile.Performance;
    }

    /// <summary>The profile's share of the measured uplink carries at least 90% of what this size and rate want (always true when unmeasured).</summary>
    private static bool UplinkAllows(ProfileInputs inputs, EncodeSample sample, int fps, string codec, BroadcastProfile profile)
    {
        if (inputs.UplinkKbps <= 0)
            return true;
        var wanted = QualityPreset.SuggestedBitrate(sample.Preset == QualityPreset.Source ? PresetForHeight(sample.Height) : sample.Preset, fps, codec) * BitrateScale(profile);
        return inputs.UplinkKbps * UplinkShare(profile) >= wanted * 0.9;
    }

    /// <summary>True when the measured uplink, not the encoder, decided the bitrate.</summary>
    private static bool UplinkCapped(ProfileInputs inputs, ProfileChoice choice, BroadcastProfile profile)
    {
        if (inputs.UplinkKbps <= 0)
            return false;
        var codec = choice.Encoder == EncoderPreference.Hevc ? "hevc" : choice.Encoder == EncoderPreference.Vp8 ? "vp8" : "h264";
        var wanted = QualityPreset.SuggestedBitrate(choice.Preset == QualityPreset.Source ? PresetForHeight(choice.Height) : choice.Preset, choice.Fps, codec) * BitrateScale(profile);
        return inputs.UplinkKbps * UplinkShare(profile) < wanted * 0.9;
    }

    private static string PresetForHeight(int height) =>
        height > 1440 ? QualityPreset.P2160 : height > 1080 ? QualityPreset.P1440 : height > 720 ? QualityPreset.P1080 : height > 480 ? QualityPreset.P720 : QualityPreset.P480;
}
