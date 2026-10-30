using System.Runtime.InteropServices;
using SkiaSharp;

namespace OpenSnip.Core;

/// <summary>
/// NativeBitmap 的 Skia 零拷贝包装、PNG 编码与 CF_DIB 字节组装。
/// 格式固定:BGRA32 非预乘。
/// </summary>
public static class BitmapConversion
{
    /// <summary>本项目的标准 Skia 图像信息:Bgra8888 + Unpremul + sRGB。</summary>
    internal static SKImageInfo CreateImageInfo(NativeBitmap bmp)
        => new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());

    /// <summary>
    /// 零拷贝包装:SKBitmap.InstallPixels 直接指到 <see cref="NativeBitmap.Scan0"/>,不复制像素。
    /// <para>销毁顺序约定(契约 §2.2):先弃 SKBitmap,再 Dispose NativeBitmap。
    /// ReleaseDelegate 传 null —— 系统回收仍归 NativeBitmap 所有。</para>
    /// </summary>
    public static SKBitmap WrapAsSkia(NativeBitmap bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        var sk = new SKBitmap();
        if (!sk.InstallPixels(CreateImageInfo(bmp), bmp.Scan0, bmp.Stride))
        {
            sk.Dispose();
            throw new InvalidOperationException("SKBitmap.InstallPixels 失败(无法包装非托管内存)。");
        }
        return sk;
    }

    /// <summary>Skia 编码 PNG(输出的 byte[] 是红线 §0.4 明确允许的例外)。</summary>
    public static byte[] EncodePng(NativeBitmap bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        using var pixmap = new SKPixmap(CreateImageInfo(bmp), bmp.Scan0, bmp.Stride);
        using var image = SKImage.FromPixels(pixmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// 手工组 CF_DIB:BITMAPINFOHEADER(40B,biHeight 正值=底朝上)+ 像素(底朝上 BGRA)。
    /// 注意 CF_DIB 行距恒为 4*Width(BI_RGB 32bpp 天然 4 字节对齐),
    /// 与 NativeBitmap 的 32 字节对齐行距无关,需重排行。
    /// </summary>
    public static unsafe byte[] GetDibBytes(NativeBitmap bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        int w = bmp.Width;
        int h = bmp.Height;
        int rowBytes = w * 4;
        var dib = new byte[40 + rowBytes * h];

        // BITMAPINFOHEADER
        WriteI32(dib, 0, 40);          // biSize
        WriteI32(dib, 4, w);           // biWidth
        WriteI32(dib, 8, h);           // biHeight:正值=底朝上
        WriteI16(dib, 12, 1);          // biPlanes
        WriteI16(dib, 14, 32);         // biBitCount
        WriteI32(dib, 16, 0);          // biCompression = BI_RGB
        WriteI32(dib, 20, rowBytes * h); // biSizeImage
        WriteI32(dib, 24, 0);          // biXPelsPerMeter
        WriteI32(dib, 28, 0);          // biYPelsPerMeter
        WriteI32(dib, 32, 0);          // biClrUsed
        WriteI32(dib, 36, 0);          // biClrImportant

        byte* src = (byte*)bmp.Scan0;
        for (int y = 0; y < h; y++)
        {
            int dstRow = h - 1 - y; // 底朝上:最后一行是源位图第 0 行
            Marshal.Copy(new IntPtr(src + (long)y * bmp.Stride), dib, 40 + dstRow * rowBytes, rowBytes);
        }
        return dib;
    }

    private static void WriteI32(byte[] b, int i, int v)
    {
        b[i] = (byte)v;
        b[i + 1] = (byte)(v >> 8);
        b[i + 2] = (byte)(v >> 16);
        b[i + 3] = (byte)(v >> 24);
    }

    private static void WriteI16(byte[] b, int i, int v)
    {
        b[i] = (byte)v;
        b[i + 1] = (byte)(v >> 8);
    }
}
