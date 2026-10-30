using System.Runtime.InteropServices;
using OpenSnip.Core;
using Xunit;

namespace OpenSnip.Core.Tests;

/// <summary>ContentProbe 非黑占比采样的数学正确性(合成位图,无环境依赖)。</summary>
public class ContentProbeTests
{
    [Fact]
    public unsafe void AllBlackBitmap_RatioIsZero()
    {
        using var bmp = NativeBitmap.Allocate(32, 32);
        Assert.Equal(0, ContentProbe.NonBlackRatio(bmp));
    }

    [Fact]
    public unsafe void PartiallyColoredBitmap_RatioInExpectedRange()
    {
        using var bmp = NativeBitmap.Allocate(64, 64);
        // 中心 10x10 填白,其余黑:期望采样占比 ≈ 100/4096 ≈ 2.4%
        byte* basePtr = (byte*)bmp.Scan0;
        for (int y = 27; y < 37; y++)
        {
            for (int x = 27; x < 37; x++)
            {
                byte* p = basePtr + (long)y * bmp.Stride + x * 4;
                p[0] = 0xFF;
                p[1] = 0xFF;
                p[2] = 0xFF;
                p[3] = 0xFF;
            }
        }
        double ratio = ContentProbe.NonBlackRatio(bmp);
        Assert.True(ratio > 0, "填色位图采样占比应 > 0");
        Assert.True(ratio < 0.2, $"采样占比 {ratio:P1} 超出预期区间(≈2.4%)");
    }

    [Fact]
    public unsafe void AlphaOnlyPixels_CountAsBlack()
    {
        // 仅 alpha 非零不算非黑(壁纸可为纯黑,alpha 判定会误伤)
        using var bmp = NativeBitmap.Allocate(16, 16);
        byte* basePtr = (byte*)bmp.Scan0;
        for (int i = 0; i < 16 * 16; i++)
        {
            basePtr[i * 4 + 3] = 0xFF;
        }
        Assert.Equal(0, ContentProbe.NonBlackRatio(bmp));
    }
}
