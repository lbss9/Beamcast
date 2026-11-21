using Beamcast.Codec;
using Xunit;

namespace Beamcast.Tests;

public class Av1CodecTests
{
    [Fact]
    public void Av1HasAWireNameAndAPreference()
    {
        Assert.Equal("av1", VideoCodec.Av1.ToWireName());
        Assert.True(VideoCodecs.TryParse("AV1", out var codec));
        Assert.Equal(VideoCodec.Av1, codec);
        Assert.True(codec.IsGpu());
        Assert.Equal(EncoderPreference.Av1, EncoderPreference.Normalize("av1"));
        // The codec list in the room page is indexed by this array.
        Assert.Equal(["Auto", "H264", "HEVC", "AV1", "VP8"], EncoderPreference.All);
    }

    [Fact]
    public void Av1NeedsLessBitrateThanHevc()
    {
        var hevc = QualityPreset.SuggestedBitrate(QualityPreset.P1080, 60, "hevc");
        var av1 = QualityPreset.SuggestedBitrate(QualityPreset.P1080, 60, "av1");
        Assert.True(av1 < hevc);
    }
}
