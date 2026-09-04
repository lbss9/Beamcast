using Xunit;

namespace Beamcast.Tests;

public class QualityPresetTests
{
    [Theory]
    [InlineData("1080p", 2560, 1440, 1920, 1080)]
    [InlineData("720p", 1920, 1080, 1280, 720)]
    [InlineData("480p", 1920, 1080, 852, 480)]
    [InlineData("1080p", 1280, 720, 1280, 720)]
    [InlineData("Source", 3840, 2160, 3840, 2160)]
    [InlineData("1440p", 3840, 2160, 2560, 1440)]
    [InlineData("2160p", 5120, 2880, 3840, 2160)]
    [InlineData("720p", 1001, 1001, 720, 720)]
    public void FitKeepsAspectAndNeverUpscales(string preset, int w, int h, int expectedW, int expectedH)
    {
        var (fw, fh) = QualityPreset.Fit(preset, w, h);
        Assert.Equal(expectedW, fw);
        Assert.Equal(expectedH, fh);
        Assert.Equal(0, fw % 2);
        Assert.Equal(0, fh % 2);
    }

    [Fact]
    public void FitHandlesNonsense()
    {
        Assert.Equal((0, 0), QualityPreset.Fit("720p", 0, 100));
        Assert.Equal("1080p", QualityPreset.Normalize("banana"));
        Assert.Equal("720p", QualityPreset.Normalize(" 720P "));
        Assert.Equal(30, QualityPreset.NormalizeFps(17));
        Assert.Equal(60, QualityPreset.NormalizeFps(60));
        Assert.Equal(QualityPreset.MinBitrateKbps, QualityPreset.ClampBitrate(-5));
    }
}
