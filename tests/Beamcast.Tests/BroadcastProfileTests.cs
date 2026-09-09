using Beamcast.Codec;
using Xunit;

namespace Beamcast.Tests;

public class BroadcastProfileTests
{
    private static EncodeSample S(string preset, int h, double ms, double fps = 60) =>
        new(preset, h * 16 / 9, h, ms, fps);

    // A mid-range GPU on a 1440p monitor: 1440p 12 ms, 1080p 7 ms, 720p 4 ms per frame.
    private static ProfileInputs MidRange(int uplink = 0) => new(2560, 1440,
        [S(QualityPreset.P1440, 1440, 12), S(QualityPreset.P1080, 1080, 7), S(QualityPreset.P720, 720, 4)], uplink, HasH264: true, HasHevc: true);

    [Fact]
    public void CandidatesNeverUpscaleAndCoverSmallSources()
    {
        Assert.Equal([QualityPreset.P1440, QualityPreset.P1080, QualityPreset.P720], BroadcastProfiles.Candidates(2560, 1440));
        Assert.Equal([QualityPreset.P1080, QualityPreset.P720], BroadcastProfiles.Candidates(1920, 1080));
        Assert.Equal([QualityPreset.Source], BroadcastProfiles.Candidates(800, 600));
    }

    [Fact]
    public void ManualOrNoSamplesGivesNothing()
    {
        Assert.Null(BroadcastProfiles.Recommend(BroadcastProfile.Manual, MidRange()));
        Assert.Null(BroadcastProfiles.Recommend(BroadcastProfile.Balanced, new ProfileInputs(1920, 1080, [], 0, true, true)));
    }

    [Fact]
    public void PerformancePicks720p30H264WithReducedBitrate()
    {
        var choice = BroadcastProfiles.Recommend(BroadcastProfile.Performance, MidRange())!;
        Assert.Equal(QualityPreset.P720, choice.Preset);
        Assert.Equal(30, choice.Fps);
        Assert.Equal(EncoderPreference.H264, choice.Encoder);
        Assert.Equal((int)Math.Round(QualityPreset.SuggestedBitrate(QualityPreset.P720, 30) * 0.75 / 250.0) * 250, choice.BitrateKbps);
    }

    [Fact]
    public void BalancedPicks1080pAnd60OnlyWhenItFits()
    {
        var choice = BroadcastProfiles.Recommend(BroadcastProfile.Balanced, MidRange())!;
        Assert.Equal(QualityPreset.P1080, choice.Preset);
        Assert.Equal(60, choice.Fps); // 7 ms ≤ 16.7 × 0.55 = 9.2 ms

        var slower = new ProfileInputs(2560, 1440, [S(QualityPreset.P1440, 1440, 20), S(QualityPreset.P1080, 1080, 11), S(QualityPreset.P720, 720, 6)], 0, true, true);
        var choice30 = BroadcastProfiles.Recommend(BroadcastProfile.Balanced, slower)!;
        Assert.Equal(QualityPreset.P1080, choice30.Preset);
        Assert.Equal(30, choice30.Fps); // 11 ms > 9.2 ms, but ≤ 33.3 × 0.55 = 18.3 ms
    }

    [Fact]
    public void QualityPicksTheLargestThatFitsAt60AndPrefersHevc()
    {
        var choice = BroadcastProfiles.Recommend(BroadcastProfile.Quality, MidRange())!;
        Assert.Equal(QualityPreset.P1440, choice.Preset); // 12 ms ≤ 16.7 × 0.75 = 12.5 ms
        Assert.Equal(60, choice.Fps);
        Assert.Equal(EncoderPreference.Hevc, choice.Encoder);
    }

    [Fact]
    public void QualityFallsBackTo30WhenNothingFitsAt60()
    {
        var slow = new ProfileInputs(2560, 1440, [S(QualityPreset.P1440, 1440, 25), S(QualityPreset.P1080, 1080, 14), S(QualityPreset.P720, 720, 13)], 0, true, false);
        var choice = BroadcastProfiles.Recommend(BroadcastProfile.Quality, slow)!;
        Assert.Equal(QualityPreset.P1440, choice.Preset); // 25 ms ≤ 33.3 × 0.75 = 25
        Assert.Equal(30, choice.Fps);
        Assert.Equal(EncoderPreference.H264, choice.Encoder);
    }

    [Fact]
    public void UplinkCapsTheBitrate()
    {
        var choice = BroadcastProfiles.Recommend(BroadcastProfile.Quality, MidRange(uplink: 6000))!;
        Assert.Equal((720, 30), (choice.Height, choice.Fps)); // 6 Mbps × 0.7 = 4.2 Mbps: 720p60 HEVC wants 4.7, 720p30 wants 3.1
        Assert.Equal(3000, choice.BitrateKbps); // 720p30 HEVC suggested 2500 × 1.25, rounded to 250
        var wide = BroadcastProfiles.Recommend(BroadcastProfile.Quality, MidRange(uplink: 100_000))!;
        Assert.True(wide.BitrateKbps > 4250);
    }

    [Fact]
    public void EncoderDroppingFramesDoesNotFit()
    {
        var starved = new ProfileInputs(1920, 1080, [S(QualityPreset.P1080, 1080, 5, fps: 30), S(QualityPreset.P720, 720, 3, fps: 60)], 0, true, false);
        var choice = BroadcastProfiles.Recommend(BroadcastProfile.Quality, starved)!;
        Assert.Equal((QualityPreset.P1080, 30), (choice.Preset, choice.Fps)); // 1080p kept only 30 of 60 frames: size wins, rate drops
        var balanced = BroadcastProfiles.Recommend(BroadcastProfile.Balanced, starved)!;
        Assert.Equal((QualityPreset.P1080, 30), (balanced.Preset, balanced.Fps));
    }

    [Fact]
    public void RecommendedPicksTheTierTheMachineEarns()
    {
        Assert.Equal(BroadcastProfile.Quality, BroadcastProfiles.PickTier(MidRange()));
        Assert.Equal(BroadcastProfile.Quality, BroadcastProfiles.Recommend(BroadcastProfile.Recommended, MidRange())!.Tier);
        // the uplink decides as much as the GPU: 12 Mbps still carries 1080p60 in HEVC, 9 Mbps does not carry 1080p at all
        var twelve = BroadcastProfiles.Recommend(BroadcastProfile.Recommended, MidRange(uplink: 12_000))!;
        Assert.Equal(BroadcastProfile.Quality, twelve.Tier);
        Assert.Equal((1080, 60, EncoderPreference.Hevc), (twelve.Height, twelve.Fps, twelve.Encoder));
        Assert.Equal(BroadcastProfile.Performance, BroadcastProfiles.PickTier(MidRange(uplink: 9_000)));
        Assert.Equal(BroadcastProfile.Performance, BroadcastProfiles.PickTier(MidRange(uplink: 3_000)));
        // Balanced itself steps 60 → 30 → smaller size as the uplink shrinks
        Assert.Equal(60, BroadcastProfiles.Recommend(BroadcastProfile.Balanced, MidRange(uplink: 20_000))!.Fps);
        Assert.Equal((1080, 30), (BroadcastProfiles.Recommend(BroadcastProfile.Balanced, MidRange(uplink: 14_000))!.Height, BroadcastProfiles.Recommend(BroadcastProfile.Balanced, MidRange(uplink: 14_000))!.Fps));
        Assert.Equal(720, BroadcastProfiles.Recommend(BroadcastProfile.Balanced, MidRange(uplink: 9_000))!.Height);
        // an encoder that only manages 1080p at 30 is Balanced
        var slow = new ProfileInputs(1920, 1080, [S(QualityPreset.P1080, 1080, 14), S(QualityPreset.P720, 720, 7)], 0, true, false);
        Assert.Equal(BroadcastProfile.Balanced, BroadcastProfiles.PickTier(slow));
        // no hardware encoder is always Performance
        var cpu = new ProfileInputs(1920, 1080, [S(QualityPreset.P1080, 1080, 30), S(QualityPreset.P720, 720, 15)], 0, false, false);
        Assert.Equal(BroadcastProfile.Performance, BroadcastProfiles.PickTier(cpu));
    }

    [Fact]
    public void NoHardwareEncoderMeansSmallVp8()
    {
        var cpu = new ProfileInputs(1920, 1080, [S(QualityPreset.P1080, 1080, 30), S(QualityPreset.P720, 720, 15)], 0, false, false);
        var choice = BroadcastProfiles.Recommend(BroadcastProfile.Quality, cpu)!;
        Assert.Equal(EncoderPreference.Vp8, choice.Encoder);
        Assert.Equal(QualityPreset.P720, choice.Preset);
        Assert.Equal(30, choice.Fps);
    }
}
