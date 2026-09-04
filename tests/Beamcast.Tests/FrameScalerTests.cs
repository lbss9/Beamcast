using Beamcast.Codec;
using Xunit;

namespace Beamcast.Tests;

public class FrameScalerTests
{
    [Fact]
    public void SameSizeIsACopy()
    {
        var src = Enumerable.Range(0, 4 * 4 * 4).Select(i => (byte)i).ToArray();
        var dst = new byte[src.Length];
        FrameScaler.Resize(src, 4, 4, dst, 4, 4);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void SolidColorStaysSolidWhenDownscaled()
    {
        const int w = 64, h = 32;
        var src = new byte[w * h * 4];
        for (var i = 0; i < src.Length; i += 4)
        {
            src[i] = 10;
            src[i + 1] = 200;
            src[i + 2] = 30;
            src[i + 3] = 255;
        }

        var dst = new byte[32 * 16 * 4];
        FrameScaler.Resize(src, w, h, dst, 32, 16);
        for (var i = 0; i < dst.Length; i += 4)
        {
            Assert.Equal(10, dst[i]);
            Assert.Equal(200, dst[i + 1]);
            Assert.Equal(30, dst[i + 2]);
            Assert.Equal(255, dst[i + 3]);
        }
    }

    [Fact]
    public void HorizontalGradientIsPreserved()
    {
        const int w = 256, h = 2;
        var src = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            src[i] = src[i + 1] = src[i + 2] = (byte)x;
            src[i + 3] = 255;
        }

        var dst = new byte[128 * 2 * 4];
        FrameScaler.Resize(src, w, h, dst, 128, 2);

        Assert.True(dst[0] <= 2);
        Assert.True(dst[(127) * 4] >= 253);
        Assert.InRange(dst[64 * 4], 126, 132);
    }

    [Fact]
    public void RejectsUndersizedBuffers()
    {
        Assert.Throws<ArgumentException>(() => FrameScaler.Resize(new byte[10], 4, 4, new byte[64], 2, 2));
        Assert.Throws<ArgumentException>(() => FrameScaler.Resize(new byte[64], 4, 4, new byte[10], 2, 2));
    }
}
