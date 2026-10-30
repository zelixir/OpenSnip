using SkiaSharp;

namespace OpenSnip.Pin;

/// <summary>
/// 贴图窗口呈现目标:一块与内容等大的 32bpp top-down DIBSection。
/// Skia 直接把像素画进 DIB 内存(预乘 BGRA,即 ULW 要求的格式),
/// 再经 UpdateLayeredWindow(ULW_ALPHA)提交到分层窗口(契约 §3.3"Skia 画进 ULW DIB")。
/// 仅允许在贴图线程使用。
/// </summary>
internal sealed class PinRenderTarget : IDisposable
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private IntPtr _defaultBmp;    // memDc 初始选中的库存位图;换 DIB 前须先选回它,才能安全删除旧 DIB
    private SKBitmap? _skiaWrapper; // 包装 DIB 内存的非托管 SKBitmap(零拷贝)
    private int _width;
    private int _height;

    public PinRenderTarget()
    {
        _memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        if (_memDc == IntPtr.Zero)
        {
            throw new OutOfMemoryException("CreateCompatibleDC 失败(贴图呈现目标初始化)");
        }
    }

    internal int Width => _width;
    internal int Height => _height;

    /// <summary>确保 DIB 尺寸匹配;尺寸变化时重建(每次提交为全量重画,无需保留旧内容)。</summary>
    public void EnsureSize(int width, int height)
    {
        if (width == _width && height == _height && _dib != IntPtr.Zero)
        {
            return;
        }
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "贴图内容尺寸必须为正");
        }

        _skiaWrapper?.Dispose();
        _skiaWrapper = null;
        if (_dib != IntPtr.Zero)
        {
            _ = NativeMethods.SelectObject(_memDc, _defaultBmp);   // 先取消选中才能安全删除
            _ = NativeMethods.DeleteObject(_dib);
            _dib = IntPtr.Zero;
            _dibBits = IntPtr.Zero;
        }

        var info = new NativeMethods.BITMAPINFO
        {
            BmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                BiSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                BiWidth = width,
                BiHeight = -height,   // 负高 = top-down
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = NativeMethods.BI_RGB,
            },
        };

        IntPtr dib = NativeMethods.CreateDIBSection(
            _memDc, ref info, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            throw new OutOfMemoryException($"CreateDIBSection 失败 ({width}x{height})");
        }

        if (_defaultBmp == IntPtr.Zero)
        {
            _defaultBmp = NativeMethods.SelectObject(_memDc, dib);   // 记住 DC 原库存位图
        }
        else
        {
            _ = NativeMethods.SelectObject(_memDc, dib);
        }

        _dib = dib;
        _dibBits = bits;
        _width = width;
        _height = height;

        // 非托管 SKBitmap 包装 DIB 内存:Skia 写入即 DIB 内容,零拷贝(红线 §0.4 合规)
        var skInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _skiaWrapper = new SKBitmap();
        if (!_skiaWrapper.InstallPixels(skInfo, bits, width * 4))
        {
            _skiaWrapper.Dispose();
            _skiaWrapper = null;
            throw new OutOfMemoryException("SKBitmap 包装 DIB 内存失败");
        }
    }

    /// <summary>
    /// 把内容位图画进 DIB 并 ULW 提交到窗口。content 须为预乘 BGRA32、尺寸与 DIB 一致(不一致时先重建 DIB)。
    /// opacityPercent ∈ [10,100]:以颜色矩阵把预乘像素整体按比例衰减,
    /// 视觉效果与 LWA_ALPHA 等价。为何不走 LWA_ALPHA:Win32 规定 SetLayeredWindowAttributes 与
    /// UpdateLayeredWindow 在同一窗口上互斥(先 ULW 后 LWA 失败),故按契约 ULW 通道内实现(见交付报告)。
    /// </summary>
    public unsafe bool Commit(IntPtr hwnd, int x, int y, SKBitmap content, byte opacityPercent)
    {
        EnsureSize(content.Width, content.Height);
        if (_skiaWrapper is null)
        {
            return false;
        }

        using (var canvas = new SKCanvas(_skiaWrapper))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint();
            if (opacityPercent < 100)
            {
                using var filter = PinRenderer.CreateOpacityFilter(opacityPercent);
                paint.ColorFilter = filter;
            }
            canvas.DrawBitmap(content, 0f, 0f, SKSamplingOptions.Default, paint);
            canvas.Flush();
        }

        // UpdateLayeredWindow:dst = 窗口左上(屏幕物理坐标),src = (0,0)
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };
        IntPtr hdcScreen = NativeMethods.GetDC(hwnd);
        if (hdcScreen == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            var dst = new NativeMethods.POINT { X = x, Y = y };
            var size = new NativeMethods.SIZE { CX = _width, CY = _height };
            var src = new NativeMethods.POINT { X = 0, Y = 0 };
            // 局部结构体在栈上,直接取址,无需 fixed
            return NativeMethods.UpdateLayeredWindow(
                hwnd, hdcScreen, &dst, &size, _memDc, &src, 0, &blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            _ = NativeMethods.ReleaseDC(hwnd, hdcScreen);
        }
    }

    public void Dispose()
    {
        _skiaWrapper?.Dispose();
        _skiaWrapper = null;
        if (_dib != IntPtr.Zero)
        {
            _ = NativeMethods.SelectObject(_memDc, _defaultBmp);
            _ = NativeMethods.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
        if (_memDc != IntPtr.Zero)
        {
            _ = NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Skia 合成辅助:重采样与透明度颜色矩阵。
/// 契约 §3.3:缩放走位图重采样(SKBitmap.Resize,quality mipmap)。
/// </summary>
internal static class PinRenderer
{
    /// <summary>
    /// mipmap 采样(线性过滤 + 线性 mip 链;契约 §3.3 "quality mipmap")。
    /// SkiaSharp 4.x 已移除 SKFilterQuality/legacy 采样,此为官方等价新 API;
    /// 大倍率缩小时走 mip 链,无摩尔纹。
    /// </summary>
    internal static readonly SKSamplingOptions MipmapSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    internal static readonly SKSamplingOptions LinearSampling = new(SKFilterMode.Linear);
    internal static readonly SKSamplingOptions MitchellSampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// 预乘 BGRA 的整幅透明度衰减矩阵(4x5 行主序,仅缩放 alpha 通道;
    /// Skia 颜色矩阵滤镜按非预乘色运算后自动回预乘,结果即 LWA_ALPHA 语义)。
    /// </summary>
    internal static SKColorFilter CreateOpacityFilter(byte opacityPercent)
    {
        float o = Math.Clamp(opacityPercent, PinWindow.MinOpacity, PinWindow.MaxOpacity) / 100f;
        var m = new float[20];
        m[0] = 1f;   // R 行
        m[6] = 1f;   // G 行
        m[12] = 1f;  // B 行
        m[18] = o;   // A 行:alpha × o
        return SKColorFilter.CreateColorMatrix(m);
    }

    /// <summary>把源包装位图(可能非预乘)绘制成同尺寸预乘 BGRA 基准位图,作为后续重采样的统一源。</summary>
    internal static SKBitmap BuildPremulBase(SKBitmap wrappedSource)
    {
        var info = new SKImageInfo(wrappedSource.Width, wrappedSource.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var dst = new SKBitmap(info);
        using (var canvas = new SKCanvas(dst))
        {
            canvas.DrawBitmap(wrappedSource, 0f, 0f, SKSamplingOptions.Default, paint: null);
            canvas.Flush();
        }
        return dst;
    }

    /// <summary>
    /// 从基准位图重采样出目标尺寸的预乘 BGRA 位图。
    /// 缩小走 mipmap(契约 §3.3 "quality mipmap";大倍率缩小无摩尔纹),
    /// 放大走纯线性(mipmap 对放大无意义,Skia 会拒绝该组合)。
    /// mipmap 组合被运行时拒绝时的兜底:高质量三次采样(Mitchell)。
    /// 失败返回 null,调用方沿用旧内容(下一格滚轮再试),不冲垮交互。
    /// </summary>
    internal static SKBitmap? ResizePremul(SKBitmap baseBitmap, int width, int height)
    {
        try
        {
            var dst = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var dstPix = dst.PeekPixels();
            if (dstPix is null)
            {
                dst.Dispose();
                return null;
            }

            bool downscale = width <= baseBitmap.Width && height <= baseBitmap.Height;
            if (baseBitmap.ScalePixels(dstPix, downscale ? MipmapSampling : LinearSampling))
            {
                return dst;
            }
            if (downscale && baseBitmap.ScalePixels(dstPix, MitchellSampling))
            {
                return dst;
            }

            dst.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }
}
