using SkiaSharp;

namespace OpenSnip.Pin;

/// <summary>
/// 贴图窗口呈现目标:一块与内容等大的 32bpp top-down DIBSection。
/// Skia 直接把像素画进 DIB 内存(预乘 BGRA,即 ULW 要求的格式),
/// 再经 UpdateLayeredWindow(ULW_ALPHA)提交到分层窗口(契约 §3.3"Skia 画进 ULW DIB")。
/// M2a(§11):支持背景层(伪透明快照/棋盘格)与瞬隐帧(伪透明重截前防止把贴图自己截进背景)。
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
            throw new OutOfMemoryException("CreateDIBSection 失败 (" + width + "x" + height + ")");
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
    /// 把内容位图画进 DIB 并 ULW 提交到窗口。content 须为预乘 BGRA32。
    /// background(契约 §11 背景层):伪透明快照或棋盘格,须与窗口同尺寸(1:1 直接铺底,不重采样);
    /// 传入时窗口整体不透明(alpha=255 整块可命中);null 时保持 M1 逐像素 alpha 行为。
    /// opacityPercent ∈ [10,100]:以颜色矩阵把内容层预乘像素整体按比例衰减(与 LWA_ALPHA 等价;
    /// 不作用于背景层 —— 伪透明下"背景恒清晰、内容随透明度淡出",符合直觉)。
    /// cropSelection(契约 §18 裁剪态):窗口内客户区选框(物理 px);非空时选框外画 50% 黑遮罩
    /// 并加 1px 红选框,空矩形(SKRectI.Empty)表示未选=全窗遮罩;null 表示非裁剪态。
    /// </summary>
    public unsafe bool Commit(IntPtr hwnd, int x, int y, SKBitmap content, byte opacityPercent, SKBitmap? background = null, SKRectI? cropSelection = null)
    {
        EnsureSize(content.Width, content.Height);
        if (_skiaWrapper is null)
        {
            return false;
        }

        using (var canvas = new SKCanvas(_skiaWrapper))
        {
            canvas.Clear(SKColors.Transparent);
            if (background is not null && background.Width == _width && background.Height == _height)
            {
                canvas.DrawBitmap(background, 0f, 0f, SKSamplingOptions.Default, paint: null);   // 背景层铺底
            }
            using var paint = new SKPaint();
            if (opacityPercent < 100)
            {
                using var filter = PinRenderer.CreateOpacityFilter(opacityPercent);
                paint.ColorFilter = filter;
            }
            canvas.DrawBitmap(content, 0f, 0f, SKSamplingOptions.Default, paint);

            // 裁剪态(契约 §18):选框外 50% 黑遮罩 + 1px 红选框;未选时全窗遮罩
            if (cropSelection is SKRectI sel)
            {
                using var mask = new SKPaint { Color = new SKColor(0, 0, 0, 128) };
                if (!sel.IsEmpty)
                {
                    canvas.DrawRect(0, 0, _width, Math.Max(0, sel.Top), mask);
                    canvas.DrawRect(0, sel.Bottom, _width, Math.Max(0, _height - sel.Bottom), mask);
                    canvas.DrawRect(0, sel.Top, Math.Max(0, sel.Left), Math.Max(0, sel.Height), mask);
                    canvas.DrawRect(sel.Right, sel.Top, Math.Max(0, _width - sel.Right), Math.Max(0, sel.Height), mask);
                    using var border = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                    canvas.DrawRect(sel.Left, sel.Top, sel.Width, sel.Height, border);
                }
                else
                {
                    canvas.DrawRect(0, 0, _width, _height, mask);
                }
            }
            canvas.Flush();
        }

        return SubmitUlW(hwnd, x, y);
    }

    /// <summary>
    /// 提交全透明帧(窗口视觉消失一瞬)。伪透明重截前使用:贴图先从屏幕上"拿开",
    /// 截屏才不会把贴图自己截进背景。调用方需在截屏后立即恢复 Commit。
    /// </summary>
    internal unsafe bool CommitTransparentFrame(IntPtr hwnd, int x, int y)
    {
        if (_dibBits == IntPtr.Zero || _width <= 0 || _height <= 0)
        {
            return false;
        }
        new Span<byte>((void*)_dibBits, _width * _height * 4).Clear();
        return SubmitUlW(hwnd, x, y);
    }

    /// <summary>ULW 提交(共用):dst = 窗口左上(屏幕物理坐标),src = (0,0)。</summary>
    private unsafe bool SubmitUlW(IntPtr hwnd, int x, int y)
    {
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
/// Skia 合成辅助:重采样、透明度颜色矩阵与棋盘生成。
/// 契约 §3.3:缩放走位图重采样(quality mipmap);§11:棋盘背景生成。
/// </summary>
internal static class PinRenderer
{
    /// <summary>
    /// mipmap 采样(线性过滤 + 线性 mip 链;契约 §3.3 "quality mipmap")。
    /// SkiaSharp 4.x 已移除 SKFilterQuality/legacy 采样,此为官方等价新 API。
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
    /// 缩小走 mipmap(契约 §3.3;大倍率缩小无摩尔纹),放大走纯线性(mipmap 对放大无意义,Skia 会拒绝该组合);
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

    /// <summary>
    /// 生成不透明棋盘格背景(契约 §11:深/浅棋盘;浅棋盘同时是伪透明的回退背景)。
    /// 格边长 8×DpiScale 物理像素;深色 #2B2B2B/#3C3C3C,浅色 #FFFFFF/#C8C8C8。
    /// </summary>
    internal static SKBitmap BuildCheckerboard(bool dark, int width, int height, float dpiScale)
    {
        int cell = Math.Max(4, (int)Math.Round(8 * dpiScale));
        SKColor baseColor = dark ? new SKColor(0x2B, 0x2B, 0x2B) : SKColors.White;
        SKColor altColor = dark ? new SKColor(0x3C, 0x3C, 0x3C) : new SKColor(0xC8, 0xC8, 0xC8);

        var dst = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(dst))
        {
            using (var basePaint = new SKPaint { Color = baseColor })
            {
                canvas.Clear(baseColor);
            }
            using var altPaint = new SKPaint { Color = altColor };
            for (int y = 0, row = 0; y < height; y += cell, row++)
            {
                for (int x = (row & 1) * cell; x < width; x += cell * 2)
                {
                    canvas.DrawRect(x, y, cell, cell, altPaint);
                }
            }
            canvas.Flush();
        }
        return dst;
    }
}
