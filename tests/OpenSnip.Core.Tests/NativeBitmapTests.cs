using System.Runtime.InteropServices;
using OpenSnip.Core;
using Xunit;

namespace OpenSnip.Core.Tests;

/// <summary>NativeBitmap 冒烟:分配/行距/区域拷贝/释放(§3.1 单测要求)。</summary>
public class NativeBitmapTests
{
    [Fact]
    public void Allocate_SetsDimensionsAndStride()
    {
        using var bmp = NativeBitmap.Allocate(10, 7);
        Assert.Equal(10, bmp.Width);
        Assert.Equal(7, bmp.Height);
        Assert.Equal(64, bmp.Stride); // 4*10=40 → 向上对齐 32 → 64
        Assert.NotEqual(IntPtr.Zero, bmp.Scan0);
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(8, 32)]
    [InlineData(9, 64)]
    [InlineData(16, 64)]
    [InlineData(1920, 7680)]
    [InlineData(3840, 15360)]
    public void Stride_AlignsTo32Bytes(int width, int expected)
        => Assert.Equal(expected, NativeBitmap.ComputeStride(width));

    [Fact]
    public unsafe void Allocate_MemoryIsZeroed()
    {
        using var bmp = NativeBitmap.Allocate(64, 4);
        var span = new Span<byte>((void*)bmp.Scan0, bmp.Stride * bmp.Height);
        Assert.True(span.IndexOfAnyExcept((byte)0) < 0, "新分配的位图应为全零。");
    }

    [Fact]
    public unsafe void CopyRegionFrom_CopiesCorrectWindow()
    {
        using var src = NativeBitmap.Allocate(20, 10);
        using var dst = NativeBitmap.Allocate(10, 5);

        // 源:每像素值 = y*20 + x(B,G,R,A 通道同值),便于校验
        byte* s = (byte*)src.Scan0;
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                byte v = (byte)(y * 20 + x);
                byte* p = s + (long)y * src.Stride + x * 4;
                p[0] = v;
                p[1] = v;
                p[2] = v;
                p[3] = 0xFF;
            }
        }

        dst.CopyRegionFrom(src, 5, 3);

        byte* d = (byte*)dst.Scan0;
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                byte expected = (byte)((3 + y) * 20 + (5 + x));
                byte* p = d + (long)y * dst.Stride + x * 4;
                Assert.Equal(expected, p[0]);
                Assert.Equal(0xFF, p[3]);
            }
        }
    }

    [Fact]
    public void CopyRegionFrom_OutOfRange_Throws()
    {
        using var src = NativeBitmap.Allocate(10, 10);
        using var dst = NativeBitmap.Allocate(5, 5);
        Assert.Throws<ArgumentOutOfRangeException>(() => dst.CopyRegionFrom(src, 6, 0)); // 6+5>10
        Assert.Throws<ArgumentOutOfRangeException>(() => dst.CopyRegionFrom(src, 0, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => dst.CopyRegionFrom(src, -1, 0));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var bmp = NativeBitmap.Allocate(8, 8);
        bmp.Dispose();
        bmp.Dispose(); // 防双释放
        GC.KeepAlive(bmp);
    }

    [Fact]
    public void Scan0_AfterDispose_Throws()
    {
        var bmp = NativeBitmap.Allocate(8, 8);
        bmp.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => _ = bmp.Scan0);
    }
}
