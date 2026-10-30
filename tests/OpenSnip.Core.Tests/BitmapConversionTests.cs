using System.Runtime.InteropServices;
using OpenSnip.Core;
using Xunit;
using SkiaSharp;

namespace OpenSnip.Core.Tests;

/// <summary>Skia 零拷贝包装 / PNG 编码 / CF_DIB 组装(§3.1:头部字段正确)。</summary>
public class BitmapConversionTests
{
    private static void FillTestPattern(NativeBitmap bmp)
    {
        unsafe
        {
            byte* basePtr = (byte*)bmp.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte* p = basePtr + (long)y * bmp.Stride + x * 4;
                    p[0] = (byte)(x + 1);        // B
                    p[1] = (byte)(y + 1);        // G
                    p[2] = 0x40;                 // R
                    p[3] = 0xFF;                 // A
                }
            }
        }
    }

    [Fact]
    public void WrapAsSkia_IsZeroCopy_WithCorrectDimensions()
    {
        using var bmp = NativeBitmap.Allocate(4, 2);
        FillTestPattern(bmp);

        var sk = BitmapConversion.WrapAsSkia(bmp); // 契约:wrap 不拷贝
        try
        {
            Assert.Equal(4, sk.Width);
            Assert.Equal(2, sk.Height);
            Assert.Equal(bmp.Scan0, sk.GetPixels()); // 像素地址就是 NativeBitmap.Scan0
            var c = sk.GetPixel(0, 0);
            Assert.Equal((byte)0x40, c.Red);
            Assert.Equal((byte)0x01, c.Green);
            Assert.Equal((byte)0x01, c.Blue);
            Assert.Equal((byte)0xFF, c.Alpha);
        }
        finally
        {
            sk.Dispose(); // 契约销毁顺序:先弃 SKBitmap,再 Dispose NativeBitmap
        }
        bmp.Dispose(); // 不崩溃即符合销毁顺序约定
    }

    [Fact]
    public void EncodePng_ProducesValidPng()
    {
        using var bmp = NativeBitmap.Allocate(4, 2);
        FillTestPattern(bmp);
        byte[] png = BitmapConversion.EncodePng(bmp);

        Assert.True(png.Length > 8);
        Assert.Equal((byte)0x89, png[0]);
        Assert.Equal((byte)0x50, png[1]); // P
        Assert.Equal((byte)0x4E, png[2]); // N
        Assert.Equal((byte)0x47, png[3]); // G

        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        Assert.Equal(4, decoded.Width);
        Assert.Equal(2, decoded.Height);
    }

    [Fact]
    public void GetDibBytes_HeaderFieldsCorrect()
    {
        using var bmp = NativeBitmap.Allocate(5, 3);
        FillTestPattern(bmp);
        byte[] dib = BitmapConversion.GetDibBytes(bmp);

        // BITMAPINFOHEADER(40B)+ 像素(BI_RGB 32bpp 行距恒为 4*Width)
        Assert.Equal(40 + 5 * 4 * 3, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));   // biSize
        Assert.Equal(5, BitConverter.ToInt32(dib, 4));    // biWidth
        Assert.Equal(3, BitConverter.ToInt32(dib, 8));    // biHeight 正值 = 底朝上
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));   // biPlanes
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));  // biBitCount
        Assert.Equal(0, BitConverter.ToInt32(dib, 16));   // biCompression = BI_RGB
        Assert.Equal(5 * 4 * 3, BitConverter.ToInt32(dib, 20)); // biSizeImage
    }

    [Fact]
    public void GetDibBytes_RowOrderIsBottomUp()
    {
        using var bmp = NativeBitmap.Allocate(2, 2);
        unsafe
        {
            byte* basePtr = (byte*)bmp.Scan0;
            // 顶行(第 0 行)像素标记 0xAA,底行标记 0xBB
            for (int x = 0; x < 2; x++)
            {
                *(basePtr + x * 4) = 0xAA;
                *(basePtr + bmp.Stride + x * 4) = 0xBB;
            }
        }
        byte[] dib = BitmapConversion.GetDibBytes(bmp);
        // 每像素 4 字节,0xAA/0xBB 写在 B 通道(偏移 0 和 4)
        // DIB 底朝上:最后 8 字节 = 源位图第 0 行(0xAA)
        Assert.Equal(0xAA, dib[^8]);
        Assert.Equal(0xAA, dib[^4]);
        // DIB 第一个像素行 = 源位图最后一行(0xBB)
        Assert.Equal(0xBB, dib[40]);
        Assert.Equal(0xBB, dib[44]);
    }
}
