// 每显示器一个遮罩窗口:WS_POPUP + WS_EX_LAYERED/TOPMOST/TOOLWINDOW,
// 背衬 = 内存 DIB(顶行在前 BGRA 预乘)零拷贝包成 SKBitmap,Skia 直画,
// UpdateLayeredWindow 全窗口提交(SPEC §3.2 简化路线:全窗口 ULW,单帧即走)。
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenSnip.Core;
using OpenSnip.Overlay.Interop;
using SkiaSharp;

namespace OpenSnip.Overlay;

/// <summary>消息宿主:OverlayWindow 把可交互消息转交给它(实现者 = SnipOverlay,单 UI 线程)。</summary>
internal interface IOverlayMessageSink
{
    /// <summary>返回 true 表示已处理(不再转 DefWindowProc)。</summary>
    bool HandleMessage(OverlayWindow window, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>单屏遮罩窗口。生命周期严格限于 Overlay 的 STA 线程上。</summary>
internal sealed class OverlayWindow : IDisposable
{
    /// <summary>窗口类名(SPEC §3.2:注册类名 "OpenSnip.Overlay")。</summary>
    internal const string ClassName = "OpenSnip.Overlay";

    // —— 类注册(全进程一次)——
    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    // 静态持有委托,防止 WndProc 被 GC 后窗口消息踩空
    private static readonly WndProcDelegate WndProcStaticDelegate = WndProcStatic;
    private static readonly Dictionary<IntPtr, OverlayWindow> Map = new();

    public IntPtr Handle { get; private set; }
    public DisplayInfo Display { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>本屏冻结画面(SnipOverlay 捕获后注入;可空 = 该屏捕获失败)。</summary>
    public NativeBitmap? FrozenBitmap { get; private set; }

    /// <summary>冻结画面的 Skia 包装(与 FrozenBitmap 共享内存,零拷贝)。</summary>
    public SKImage? FrozenImage => _frozenImage;

    internal IOverlayMessageSink? Sink { get; set; }

    private readonly IntPtr _screenDc;      // GetDC(null),ULW 的 hdcDst
    private readonly IntPtr _memDc;         // CreateCompatibleDC,已选入 DIB
    private readonly IntPtr _dib;           // CreateDIBSection 句柄
    private readonly SKBitmap _backbuffer;  // wrap DIB 位(不拷贝)
    private readonly SKCanvas _canvas;      // 直接画进背衬(SPEC §3.2 性能要点)
    private SKImage? _frozenImage;
    private SKBitmap? _frozenSkBitmap;      // WrapAsSkia 的包装,保活与销毁排序用
    private SKBitmap? _frozenPremulBitmap;  // 一次性预乘转存的拥有者(拖拽路径快速 blit 用)
    private bool _disposed;

    private OverlayWindow(DisplayInfo display, IntPtr hwnd, IntPtr screenDc, IntPtr memDc,
        IntPtr dib, IntPtr bits, SKBitmap backbuffer)
    {
        Display = display;
        Width = display.Bounds.Width;
        Height = display.Bounds.Height;
        Handle = hwnd;
        _screenDc = screenDc;
        _memDc = memDc;
        _dib = dib;
        _backbuffer = backbuffer;
        _canvas = new SKCanvas(_backbuffer);
    }

    /// <summary>在当前线程上创建遮罩窗口(必须已在 Overlay STA 线程,且线程已设 PMv2 上下文)。</summary>
    public static OverlayWindow Create(DisplayInfo display)
    {
        EnsureClassRegistered();

        int w = display.Bounds.Width;
        int h = display.Bounds.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"显示器尺寸非法:{display.DeviceName} {w}x{h}");

        // 窗口尺寸 = 该屏物理 Bounds(SPEC §3.2);线程为 PMv2,坐标即物理像素
        IntPtr hwnd = NativeMethods.CreateWindowExW(
            Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW,
            ClassName, null, Win32.WS_POPUP,
            display.Bounds.X, display.Bounds.Y, w, h,
            IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW 失败:{display.DeviceName}, Win32Error={Marshal.GetLastWin32Error()}");

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr dib = IntPtr.Zero, bits = IntPtr.Zero;
        SKBitmap? backbuffer = null;
        try
        {
            // 顶行在前(top-down)32bpp DIB:行序与 Skia 一致,提交零转换
            var bmi = new BITMAPINFO { bmiHeader = BITMAPINFOHEADER.TopDown32(w, h) };
            dib = NativeMethods.CreateDIBSection(memDc, ref bmi, Win32.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException($"CreateDIBSection 失败:{display.DeviceName}, Win32Error={Marshal.GetLastWin32Error()}");
            NativeMethods.SelectObject(memDc, dib);

            // ULW 要求预乘 alpha 的 BGRA → SKColorType.Bgra8888 + SKAlphaType.Premul
            backbuffer = new SKBitmap();
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (!backbuffer.InstallPixels(info, bits, w * 4))
                throw new InvalidOperationException($"SKBitmap.InstallPixels 失败:{display.DeviceName}");

            var win = new OverlayWindow(display, hwnd, screenDc, memDc, dib, bits, backbuffer);
            lock (Map) Map[hwnd] = win;
            return win;
        }
        catch
        {
            backbuffer?.Dispose();
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            NativeMethods.DestroyWindow(hwnd);
            throw;
        }
    }

    /// <summary>注入本屏冻结画面(Core §2.2 契约:WrapAsSkia 零拷贝包装)。</summary>
    public void SetFrozen(NativeBitmap bmp)
    {
        // 先弃旧 SK 包装(§2.2 销毁顺序:先弃 SKBitmap,再 Dispose NativeBitmap)
        DropFrozenSkia();

        FrozenBitmap = bmp;
        if (bmp is null) return;

        // §2.2 契约:WrapAsSkia 零拷贝包装(BGRA32 非预乘)
        _frozenSkBitmap = BitmapConversion.WrapAsSkia(bmp);
        var pixmap = _frozenSkBitmap.PeekPixels()
            ?? throw new InvalidOperationException("WrapAsSkia 返回的 SKBitmap 无像素映射");
        using var unpremul = SKImage.FromPixels(pixmap);

        // 非预乘源逐帧 blit 走 Skia 慢速管线(实测 2K:unpremul ≈6.7ms/帧 vs premul ≈0.8ms/帧),
        // 会话开始时一次性预乘转存(同 Pin.BuildPremulBase 模式),拖拽路径走快速 blit。
        var info = new SKImageInfo(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var premul = new SKBitmap(info);
        using (var c = new SKCanvas(premul))
        {
            var full = new SKRect(0, 0, bmp.Width, bmp.Height);
            c.DrawImage(unpremul, full, full, SKSamplingOptions.Default, null);
            c.Flush();
        }
        _frozenPremulBitmap = premul;
        // SKImage.FromPixels 只引用 premul 位图内存,不做拷贝(premul 位图由本类持有保活)
        _frozenImage = SKImage.FromPixels(premul.PeekPixels()
            ?? throw new InvalidOperationException("premul SKBitmap 无像素映射"));
    }

    /// <summary>渲染一帧并提交 ULW(调用方负责计时/PerfLog 打点)。</summary>
    public void RenderFrame(OverlayRenderer renderer, in RenderModel model)
    {
        renderer.DrawFrame(_canvas, model);
        Commit();
    }

    /// <summary>把背衬 DIB 全窗口提交到屏幕(§3.2 简化:不做失效矩形,单帧全量)。</summary>
    public bool Commit()
    {
        var dstPos = new POINT(Display.Bounds.X, Display.Bounds.Y);
        var size = new SIZE(Width, Height);
        var srcPos = new POINT(0, 0);
        var blend = BLENDFUNCTION.SrcOverPerPixel();
        return NativeMethods.UpdateLayeredWindow(Handle, _screenDc, ref dstPos, ref size,
            _memDc, ref srcPos, 0, ref blend, Win32.ULW_ALPHA);
    }

    /// <summary>按 HWND 查窗口(消息合并用)。</summary>
    public static OverlayWindow? FromHandle(IntPtr hwnd)
    {
        lock (Map) return Map.TryGetValue(hwnd, out var w) ? w : null;
    }

    private static IntPtr WndProcStatic(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        OverlayWindow? w;
        lock (Map) Map.TryGetValue(hWnd, out w);
        if (w?.Sink is IOverlayMessageSink sink && sink.HandleMessage(w, msg, wParam, lParam))
            return IntPtr.Zero;
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = Win32.CS_DBLCLKS,          // 需要双击消息
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcStaticDelegate),
                hInstance = NativeMethods.GetModuleHandleW(null),
                hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, Win32.OCR_CROSS),
                hbrBackground = IntPtr.Zero,       // ULW 窗口无背景刷
                lpszClassName = ClassName,
            };
            ushort atom = NativeMethods.RegisterClassExW(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS:容忍(同进程二次初始化)
                    throw new InvalidOperationException($"RegisterClassExW 失败, Win32Error={err}");
            }
            _classRegistered = true;
        }
    }

    private void DropFrozenSkia()
    {
        // §2.2 约定:先弃 SK 侧包装,NativeBitmap 由所有者(SnipOverlay)统一 Dispose
        _frozenImage?.Dispose();
        _frozenImage = null;
        _frozenSkBitmap?.Dispose();
        _frozenSkBitmap = null;
        _frozenPremulBitmap?.Dispose();
        _frozenPremulBitmap = null;
        FrozenBitmap = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DropFrozenSkia();
        _canvas.Dispose();
        _backbuffer.Dispose();   // 只解绑 DIB 内存,不释放(NativeMemory 属 DIB)

        if (Handle != IntPtr.Zero)
        {
            lock (Map) Map.Remove(Handle);
            NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        NativeMethods.DeleteObject(_dib);
        NativeMethods.DeleteDC(_memDc);
        NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
