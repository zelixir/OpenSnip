using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// 图像解码(WPF WIC):PNG/JPG/BMP/GIF/TIFF 流 → BGRA32 非预乘 NativeBitmap。
/// 像素经 CopyPixels 直接写非托管内存,全程不经 byte[](红线 §0.4)。
/// </summary>
public static class ImageDecoder
{
    /// <summary>将图像流解码为标准 BGRA32 非预乘位图。流读毕即弃(OnLoad)。</summary>
    public static NativeBitmap DecodeToNativeBitmap(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidDataException("图像流中没有可解码的帧");
        return FromBitmapSource(frame);
    }

    /// <summary>BitmapSource → BGRA32 NativeBitmap(非 Bgra32 先做格式转换)。</summary>
    public static NativeBitmap FromBitmapSource(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource src = source;
        if (src.Format != PixelFormats.Bgra32)
        {
            var conv = new FormatConvertedBitmap();
            conv.BeginInit();
            conv.Source = src;
            conv.DestinationFormat = PixelFormats.Bgra32;
            conv.EndInit();
            TryFreeze(conv);
            src = conv;
        }
        TryFreeze(src);

        var bmp = NativeBitmap.Allocate(src.PixelWidth, src.PixelHeight);
        try
        {
            // 像素直接落非托管内存(红线 §0.4:禁止 byte[] 承载全屏数据)
            src.CopyPixels(Int32Rect.Empty, bmp.Scan0, src.PixelHeight * bmp.Stride, bmp.Stride);
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
        return bmp;
    }

    private static void TryFreeze(Freezable f)
    {
        try { if (!f.IsFrozen && f.CanFreeze) f.Freeze(); } catch { /* 跨线程访问失败不致命 */ }
    }
}
