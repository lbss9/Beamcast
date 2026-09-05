using Xunit;

namespace Beamcast.Tests;

public class LetterboxTests
{
    [Fact]
    public void SameAspectFillsTheTarget()
    {
        Assert.Null(QualityPreset.Letterbox(1920, 1080, 1280, 720));
        Assert.Null(QualityPreset.Letterbox(2560, 1080, 2560, 1080));
    }

    [Fact]
    public void TallWindowIntoWideStreamGetsPillarbox()
    {
        var box = QualityPreset.Letterbox(800, 1200, 1920, 1080);
        Assert.NotNull(box);
        var (x, y, w, h) = box!.Value;
        Assert.Equal(1080, h);
        Assert.Equal(720, w);
        Assert.Equal(0, y);
        Assert.Equal(600, x);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, x % 2);
    }

    [Fact]
    public void UltrawideIntoSixteenNineGetsLetterbox()
    {
        var box = QualityPreset.Letterbox(2560, 1080, 1920, 1080);
        Assert.NotNull(box);
        var (x, y, w, h) = box!.Value;
        Assert.Equal(1920, w);
        Assert.Equal(810, h);
        Assert.Equal(0, x);
        Assert.Equal(134, y);
        Assert.True(y + h <= 1080);
    }

    [Fact]
    public void DegenerateSizesAreIgnored()
    {
        Assert.Null(QualityPreset.Letterbox(0, 100, 1920, 1080));
        Assert.Null(QualityPreset.Letterbox(100, 100, 0, 0));
    }

    [Fact]
    public void BoxNeverExceedsTheTarget()
    {
        foreach (var (fw, fh) in new[] { (3, 5000), (5000, 3), (1366, 768), (1281, 721), (4096, 2160) })
        {
            var box = QualityPreset.Letterbox(fw, fh, 1280, 720);
            if (box is null)
                continue;
            var (x, y, w, h) = box.Value;
            Assert.True(x >= 0 && y >= 0 && x + w <= 1280 && y + h <= 720, $"{fw}x{fh} -> {x},{y} {w}x{h}");
            Assert.Equal(0, w % 2);
            Assert.Equal(0, h % 2);
        }
    }
}
