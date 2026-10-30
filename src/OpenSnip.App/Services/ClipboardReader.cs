using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OpenSnip.App.Interop;
using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// 剪贴板图像读取(CF_DIB / "PNG" 注册格式 / CF_HDROP 文件列表)。
/// 契约 §2.6 只定义了写入(Core.ClipboardWriter);读取由 App 侧补齐(SPEC §3.4:贴图=读剪贴板图像,
/// v1 至少支持图像与文件路径)。像素数据全程驻非托管内存,不经 byte[](红线 §0.4)。
/// 优先级:PNG(无损)> CF_DIB > 文件列表。调用线程必须是 STA(经 StaWorker 投递)。
/// </summary>
public static class ClipboardReader
{
    /// <summary>文件列表贴图上限(防止一次贴几十张)。</summary>
    private const int MaxDropImages = 8;

    private static readonly string[] SupportedExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    /// <summary>读取剪贴板中的图像(所有权归调用者;贴图场景下交给 PinWindow,其 Close 时 Dispose)。</summary>
    public static List<NativeBitmap> ReadImages()
    {
        var images = new List<NativeBitmap>();
        var dropFiles = new List<string>();

        if (!OpenClipboardWithRetry())
        {
            PerfLog.Log("App", "paste:clipboard-open-failed(重试后仍打不开,可能被其他进程占用)");
            return images;
        }

        try
        {
            var pngFormat = NativeMethods.RegisterClipboardFormatW("PNG");
            if (pngFormat != 0 && NativeMethods.IsClipboardFormatAvailable(pngFormat))
            {
                var bmp = TryReadPngFormat(pngFormat);
                if (bmp is not null)
                {
                    images.Add(bmp);
                    return images;
                }
            }
            if (images.Count == 0 && NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB))
            {
                var bmp = TryReadDib();
                if (bmp is not null)
                {
                    images.Add(bmp);
                    return images;
                }
            }
            if (images.Count == 0 && NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_HDROP))
            {
                CollectDropFiles(dropFiles);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        // 文件列表在剪贴板会话外解码(数据在磁盘上,无需占着剪贴板)
        foreach (var file in dropFiles)
        {
            var bmp = TryDecodeFile(file);
            if (bmp is not null)
            {
                images.Add(bmp);
                if (images.Count >= MaxDropImages)
                {
                    break;
                }
            }
        }
        return images;
    }

    private static bool OpenClipboardWithRetry()
    {
        for (int attempt = 1; attempt <= 10; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                return true;
            }
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>读注册格式 "PNG"(来自 OpenSnip 自身或对等应用)→ WIC 解码(流直接铺在剪贴板内存上)。</summary>
    private static unsafe NativeBitmap? TryReadPngFormat(uint format)
    {
        var h = NativeMethods.GetClipboardData(format);
        if (h == IntPtr.Zero)
        {
            return null;
        }
        var p = NativeMethods.GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            long size = (long)NativeMethods.GlobalSize(h);
            if (size <= 0)
            {
                return null;
            }
            using var stream = new UnmanagedMemoryStream((byte*)p, size); // 零拷贝解码
            return ImageDecoder.DecodeToNativeBitmap(stream);
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"paste:png-format-decode-failed {ex.Message}");
            return null;
        }
        finally
        {
            NativeMethods.GlobalUnlock(h);
        }
    }

    /// <summary>读 CF_DIB:手工解析 BITMAPINFOHEADER,行级拷入 NativeBitmap(支持 32bpp BI_RGB/BI_BITFIELDS 与 24bpp)。</summary>
    private static unsafe NativeBitmap? TryReadDib()
    {
        var h = NativeMethods.GetClipboardData(NativeMethods.CF_DIB);
        if (h == IntPtr.Zero)
        {
            return null;
        }
        var p = NativeMethods.GlobalLock(h);
        if (p == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            long total = (long)NativeMethods.GlobalSize(h);
            var header = (BITMAPINFOHEADER*)p;
            int biSize = (int)header->biSize;
            int width = header->biWidth;
            int height = header->biHeight;
            int bitCount = header->biBitCount;
            uint compression = header->biCompression;

            if (biSize < 40 || width <= 0 || height == 0
                || (bitCount != 32 && bitCount != 24)
                || (compression != 0 && compression != 3)) // BI_RGB / BI_BITFIELDS
            {
                PerfLog.Log("App", $"paste:dib-unsupported biSize={biSize} {width}x{height} bpp={bitCount} comp={compression}");
                return null;
            }

            int rows = Math.Abs(height);
            bool bottomUp = height > 0;
            // V1 头(40B)+ BI_BITFIELDS 时后跟 3 个掩码 DWORD;V4+ 头掩码已含在头部内
            int pixelOffset = biSize + ((compression == 3 && biSize <= 40) ? 12 : 0);
            long stride = ((long)width * (bitCount / 8) + 3) & ~3L;
            if (pixelOffset + stride * (long)rows > total)
            {
                PerfLog.Log("App", $"paste:dib-size-mismatch need={pixelOffset + stride * (long)rows} have={total}");
                return null;
            }

            var bmp = NativeBitmap.Allocate(width, rows);
            try
            {
                byte* srcBase = (byte*)p + pixelOffset;
                for (int y = 0; y < rows; y++)
                {
                    byte* srcRow = srcBase + (long)(bottomUp ? rows - 1 - y : y) * stride;
                    byte* dstRow = (byte*)bmp.Scan0 + (long)y * bmp.Stride;
                    if (bitCount == 32)
                    {
                        Buffer.MemoryCopy(srcRow, dstRow, (ulong)bmp.Stride, (ulong)((long)width * 4));
                    }
                    else // 24bpp → 32bpp 展开,alpha 置满
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte* s = srcRow + x * 3;
                            byte* d = dstRow + x * 4;
                            d[0] = s[0];
                            d[1] = s[1];
                            d[2] = s[2];
                            d[3] = 255;
                        }
                    }
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
        }
        finally
        {
            NativeMethods.GlobalUnlock(h);
        }
    }

    /// <summary>枚举 CF_HDROP 文件列表(仅收集名字;解码在 CloseClipboard 之后)。</summary>
    private static void CollectDropFiles(List<string> files)
    {
        var h = NativeMethods.GetClipboardData(NativeMethods.CF_HDROP);
        if (h == IntPtr.Zero)
        {
            return;
        }
        uint count = NativeMethods.DragQueryFileW(h, 0xFFFF_FFFF, null, 0);
        for (uint i = 0; i < count && files.Count < MaxDropImages * 4; i++)
        {
            uint len = NativeMethods.DragQueryFileW(h, i, null, 0);
            if (len == 0)
            {
                continue;
            }
            var sb = new StringBuilder((int)len + 1);
            if (NativeMethods.DragQueryFileW(h, i, sb, (uint)sb.Capacity) > 0)
            {
                var name = sb.ToString();
                var ext = System.IO.Path.GetExtension(name);
                if (SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(name);
                }
            }
        }
    }

    private static NativeBitmap? TryDecodeFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return ImageDecoder.DecodeToNativeBitmap(fs);
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"paste:file-decode-failed {path} {ex.Message}");
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
