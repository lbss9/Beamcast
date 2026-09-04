namespace Beamcast.Codec;

/// <summary>Bilinear BGRA resampler used to bring captured frames down to the chosen preset.</summary>
public static class FrameScaler
{
    /// <summary>
    /// Resamples <paramref name="source"/> (srcW×srcH, 4 bytes per pixel, tightly packed) into
    /// <paramref name="destination"/> (dstW×dstH). The destination must hold at least dstW*dstH*4 bytes.
    /// </summary>
    public static unsafe void Resize(
        byte[] source,
        int srcW,
        int srcH,
        byte[] destination,
        int dstW,
        int dstH
    )
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            throw new ArgumentOutOfRangeException(nameof(dstW), "Dimensions must be positive.");
        if (source.Length < srcW * srcH * 4)
            throw new ArgumentException("Source buffer too small.", nameof(source));
        if (destination.Length < dstW * dstH * 4)
            throw new ArgumentException("Destination buffer too small.", nameof(destination));

        if (srcW == dstW && srcH == dstH)
        {
            Buffer.BlockCopy(source, 0, destination, 0, srcW * srcH * 4);
            return;
        }

        // Precompute the horizontal sample positions once; rows reuse them.
        var x0 = new int[dstW];
        var fx = new int[dstW];
        var xScale = srcW / (double)dstW;
        for (var x = 0; x < dstW; x++)
        {
            var sx = Math.Max(0, (x + 0.5) * xScale - 0.5);
            var ix = (int)sx;
            if (ix >= srcW - 1)
            {
                ix = srcW - 1;
                sx = ix;
            }
            x0[x] = ix;
            fx[x] = (int)((sx - ix) * 256);
        }

        var yScale = srcH / (double)dstH;
        var srcStride = srcW * 4;
        var dstStride = dstW * 4;

        Parallel.For(
            0,
            dstH,
            y =>
            {
                var sy = Math.Max(0, (y + 0.5) * yScale - 0.5);
                var iy = (int)sy;
                if (iy >= srcH - 1)
                {
                    iy = srcH - 1;
                    sy = iy;
                }
                var fy = (int)((sy - iy) * 256);
                var iy1 = Math.Min(iy + 1, srcH - 1);

                fixed (byte* src = source)
                fixed (byte* dst = destination)
                {
                    var row0 = src + iy * srcStride;
                    var row1 = src + iy1 * srcStride;
                    var outRow = dst + y * dstStride;

                    for (var x = 0; x < dstW; x++)
                    {
                        var ix = x0[x];
                        var ix1 = Math.Min(ix + 1, srcW - 1);
                        var wx = fx[x];
                        var p00 = row0 + ix * 4;
                        var p01 = row0 + ix1 * 4;
                        var p10 = row1 + ix * 4;
                        var p11 = row1 + ix1 * 4;
                        var o = outRow + x * 4;

                        for (var c = 0; c < 4; c++)
                        {
                            var top = p00[c] * (256 - wx) + p01[c] * wx;
                            var bottom = p10[c] * (256 - wx) + p11[c] * wx;
                            o[c] = (byte)((top * (256 - fy) + bottom * fy + 32768) >> 16);
                        }
                    }
                }
            }
        );
    }
}
