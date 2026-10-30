// 遮罩选区主控(SPEC §2.8 契约冻结区):
//   Start() 创建内部 STA 线程 → 逐屏回调 capture 冻结画面 → 每屏建 ULW 遮罩窗口 → GetMessage 循环。
//   事件 Confirmed/Cancelled 均在内部线程触发;Close() 线程安全(视同 Cancelled)。
//   PerfLog 打点:capture 耗时 / Start()→首帧 ULW 提交 / 确认→裁剪完成(§3.2)。
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenSnip.Core;
using OpenSnip.Overlay.Interop;

namespace OpenSnip.Overlay;

public sealed class SnipOverlay : IDisposable, IOverlayMessageSink
{
    private readonly IReadOnlyList<DisplayInfo> _displays;
    private readonly Func<DisplayInfo, NativeBitmap> _capture;
    private readonly List<OverlayWindow> _windows = new();
    private readonly Dictionary<int, NativeBitmap?> _frozen = new();  // DisplayInfo.Index → 冻结位图
    private readonly HashSet<IntPtr> _selfHwnds = new();
    private readonly OverlayRenderer _renderer = new();
    private readonly Dictionary<int, IntPtr> _cursorCache = new();    // 标准光标句柄缓存
    private readonly Stopwatch _startSw = new();

    private OverlaySession? _session;
    private Thread? _thread;
    private volatile bool _closeRequested;
    private bool _confirmed;
    private long _firstHwnd;   // Interlocked 读写;Close() 用它投递 WM_CLOSE
    private bool _started;
    private bool _disposed;

    /// <summary>Enter/双击确认;在内部线程触发;触发后遮罩自动退出。</summary>
    public event Action<SnipResult>? Confirmed;

    /// <summary>Esc/右键/外部 Close 取消;在内部线程触发。</summary>
    public event Action? Cancelled;

    public SnipOverlay(IReadOnlyList<DisplayInfo> displays, Func<DisplayInfo, NativeBitmap> capture)
    {
        _displays = displays ?? throw new ArgumentNullException(nameof(displays));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        if (_displays.Count == 0) throw new ArgumentException("displays 不能为空", nameof(displays));
    }

    /// <summary>创建内部 STA 线程,冻结画面、建遮罩窗口并显示。只能调用一次。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("SnipOverlay.Start 只能调用一次");
        _started = true;

        _startSw.Start();
        var t = new Thread(ThreadBody)
        {
            Name = "OpenSnip.Overlay",
            IsBackground = true, // 进程退出不因遮罩线程挂起
        };
        t.SetApartmentState(ApartmentState.STA); // SPEC §2.8:事件在内部 STA 线程触发
        _thread = t;
        t.Start();
    }

    /// <summary>线程安全强制关闭(视同 Cancelled)。</summary>
    public void Close()
    {
        _closeRequested = true;
        long hwnd = Interlocked.Read(ref _firstHwnd);
        if (hwnd != 0)
            NativeMethods.PostMessageW(new IntPtr(hwnd), WM.CLOSE, IntPtr.Zero, IntPtr.Zero);
        // 窗口尚未创建时:线程建窗循环与消息循环入口都会检查 _closeRequested
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        var t = _thread;
        if (t is not null && t.IsAlive && t.ManagedThreadId != Environment.CurrentManagedThreadId)
            t.Join(3000); // 最多等 3s 清理;超时交由后台线程自然退出(IsBackground)
    }

    // —————————————————— 内部线程 ——————————————————

    private void ThreadBody()
    {
        try
        {
            // Per-Monitor V2:CreateWindow/GetCursorPos/消息坐标全部为物理像素
            NativeMethods.SetThreadDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            if (_closeRequested) return; // finally → Teardown → Cancelled

            // 1) 冻结画面:逐屏回调 capture(SPEC §3.2:Start() 后先回调)
            foreach (var d in _displays)
            {
                NativeBitmap? bmp = null;
                try
                {
                    var elapsed = PerfLog.Measure("Overlay.Capture", $"display={d.Index}", () => bmp = _capture(d));
                    PerfLog.Log("Overlay.Capture",
                        $"display={d.Index} elapsed={elapsed.TotalMilliseconds:F1}ms size={bmp!.Width}x{bmp!.Height}");
                    EnsureOpaque(bmp);
                    _frozen[d.Index] = bmp;
                }
                catch (Exception ex)
                {
                    _frozen[d.Index] = null;
                    PerfLog.Log("Overlay.Capture", $"display={d.Index} FAILED: {ex.Message}");
                }
            }

            // 2) 每屏一个遮罩窗口(SPEC §3.2)
            foreach (var d in _displays)
            {
                var w = OverlayWindow.Create(d);
                if (_frozen.TryGetValue(d.Index, out var bmp) && bmp is not null)
                    w.SetFrozen(bmp); // WrapAsSkia 零拷贝包装
                w.Sink = this;
                _windows.Add(w);
                _selfHwnds.Add(w.Handle);
                Interlocked.CompareExchange(ref _firstHwnd, w.Handle.ToInt64(), 0);
                if (_closeRequested) break;
            }
            if (_closeRequested && _windows.Count == 0) return;

            _session = new OverlaySession(_displays, _frozen, _selfHwnds);

            // 初始光标位置(避免首帧无准线/无高亮)
            if (NativeMethods.GetCursorPos(out var cp))
                _session.OnMouseMove(new PxPoint(cp.X, cp.Y));

            // 首帧:渲染 → ULW → 显示(SW_SHOWNA 避免连环激活闪烁)
            foreach (var w in _windows)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    w.RenderFrame(_renderer, _session.BuildModel(w));
                    PerfLog.Log("Overlay.FirstFrame",
                        $"display={w.Display.Index} draw+ULW={sw.Elapsed.TotalMilliseconds:F2}ms");
                    if (!w.Commit())
                        PerfLog.Log("Overlay.FirstFrame",
                            $"display={w.Display.Index} ULW failed Win32Error={Marshal.GetLastWin32Error()}");
                }
                catch (Exception ex)
                {
                    PerfLog.Log("Overlay.FirstFrame", $"display={w.Display.Index} FAILED: {ex.Message}");
                }
                NativeMethods.ShowWindow(w.Handle, Win32.SW_SHOWNA);
            }

            // 主屏窗口接管前台+焦点,保证键盘消息
            var primary = _windows.Find(x => x.Display.IsPrimary) ?? _windows[0];
            NativeMethods.SetForegroundWindow(primary.Handle);
            NativeMethods.SetFocus(primary.Handle);

            PerfLog.Log("Overlay.FirstFrame",
                $"startToFirstULW={_startSw.Elapsed.TotalMilliseconds:F1}ms displays={_windows.Count}");

            if (_closeRequested) NativeMethods.PostQuitMessage(0);

            // 3) 消息循环(SPEC §3.2:GetMessage,所有窗口共线程)
            while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            PerfLog.Log("Overlay.Lifecycle", $"thread crashed: {ex}");
        }
        finally
        {
            Teardown();
        }
    }

    private void Teardown()
    {
        try
        {
            foreach (var w in _windows) w.Dispose(); // 窗口/DIB/SK 包装(冻结位图的 SK 包装先弃,§2.2 顺序)
            _windows.Clear();
            foreach (var kv in _frozen)
                kv.Value?.Dispose();                 // 最后释放 NativeBitmap
            _frozen.Clear();
        }
        catch (Exception ex)
        {
            PerfLog.Log("Overlay.Lifecycle", $"teardown error: {ex.Message}");
        }

        if (!_confirmed)
            Cancelled?.Invoke(); // 视同 Cancelled(§2.8:外部 Close() 亦是)
    }

    /// <summary>防御:捕获位图 alpha 可能为 0/未定义(BitBlt/DXGI 驱动差异),ULW 预乘管线需要不透明源。
    /// 先只读扫描,已全不透明则零成本返回。</summary>
    private static unsafe void EnsureOpaque(NativeBitmap bmp)
    {
        int pixels = bmp.Width * bmp.Height;
        byte* basePtr = (byte*)bmp.Scan0;
        for (int i = 0; i < pixels; i++)
        {
            if (basePtr[(long)i * 4 + 3] == 255) continue;
            var sw = Stopwatch.StartNew();
            for (int j = i; j < pixels; j++) basePtr[(long)j * 4 + 3] = 255;
            PerfLog.Log("Overlay.Capture", $"alpha normalized in {sw.Elapsed.TotalMilliseconds:F2}ms (first bad at {i})");
            return;
        }
    }

    // —————————————————— 消息宿主(仅内部线程) ——————————————————

    // 显式接口实现:接口与参数类型均为 internal,不外泄到公共 API
    bool IOverlayMessageSink.HandleMessage(OverlayWindow w, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM.PAINT:
                    NativeMethods.ValidateRect(w.Handle, IntPtr.Zero);
                    return true;

                case WM.ERASEBKGND:
                    return true;

                case WM.CLOSE:
                    NativeMethods.PostQuitMessage(0); // finally → Teardown → Cancelled
                    return true;

                case WM.SETCURSOR:
                    int ht = unchecked((int)(long)lParam) & 0xFFFF; // 低 16 位 = 命中测试码
                    if (ht == Win32.HTCLIENT)
                    {
                        NativeMethods.SetCursor(LoadedCursor(_session?.PickCursorId() ?? Win32.OCR_CROSS));
                        return true;
                    }
                    return false;

                case WM.MOUSEMOVE:
                {
                    _session?.OnMouseMove(ToVirtual(w, lParam));
                    CoalescePendingMoves(); // 合并队列中的连续移动,一次渲染
                    RenderAll();
                    NativeMethods.SetCursor(LoadedCursor(_session?.PickCursorId() ?? Win32.OCR_CROSS));
                    return true;
                }

                case WM.LBUTTONDOWN:
                    NativeMethods.SetCapture(w.Handle);
                    _session?.OnLeftButtonDown(ToVirtual(w, lParam));
                    RenderAll();
                    return true;

                case WM.LBUTTONUP:
                    NativeMethods.ReleaseCapture();
                    _session?.OnLeftButtonUp(ToVirtual(w, lParam));
                    RenderAll();
                    return true;

                case WM.LBUTTONDBLCLK:
                    // 有选区确认选区;无选区确认高亮窗口(Snipaste 行为)
                    if (_session is { } s && s.Selection is null && !s.TrySelectHover()) return true;
                    ConfirmAndExit();
                    return true;

                case WM.RBUTTONDOWN:
                    // 右键取消(对齐 Snipaste;SPEC 未提及,见交付说明)
                    NativeMethods.PostQuitMessage(0);
                    return true;

                case WM.MOUSEWHEEL:
                    if (_session?.OnWheel((short)((long)wParam >> 16)) == true)
                        RenderAll();
                    return true;

                case WM.KEYDOWN:
                case WM.SYSKEYDOWN:
                    HandleKey((int)wParam);
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // 消息处理异常不拖垮整个遮罩(仅丢弃该帧)
            PerfLog.Log("Overlay.Message", $"msg=0x{msg:X} error: {ex.Message}");
            return false;
        }
    }

    private void HandleKey(int vk)
    {
        switch (vk)
        {
            case Win32.VK_ESCAPE:
                NativeMethods.PostQuitMessage(0);
                return;
            case Win32.VK_RETURN:
                if (_session is { } s && s.Selection is null && !s.TrySelectHover()) return;
                ConfirmAndExit();
                return;
            case Win32.VK_LEFT:
            case Win32.VK_UP:
            case Win32.VK_RIGHT:
            case Win32.VK_DOWN:
            {
                int step = (NativeMethods.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0 ? 10 : 1;
                int dx = vk == Win32.VK_LEFT ? -step : vk == Win32.VK_RIGHT ? step : 0;
                int dy = vk == Win32.VK_UP ? -step : vk == Win32.VK_DOWN ? step : 0;
                if (_session?.Nudge(dx, dy) == true) RenderAll();
                return;
            }
        }
    }

    /// <summary>确认:裁剪(PerfLog 打点)→ 隐藏窗口 → 触发 Confirmed(内部线程)→ 退出循环。</summary>
    private void ConfirmAndExit()
    {
        var s = _session;
        if (s is null || _confirmed) return;

        SnipResult? result = null;
        var elapsed = PerfLog.Measure("Overlay.Confirm", "crop", () => result = s.BuildResult());
        if (result is null) return; // 无有效选区

        _confirmed = true;
        PerfLog.Log("Overlay.Confirm",
            $"crop done in {elapsed.TotalMilliseconds:F1}ms rect={result.PhysicalRect.X},{result.PhysicalRect.Y} " +
            $"{result.PhysicalRect.Width}x{result.PhysicalRect.Height} display={result.Display.Index}");

        // 先隐藏再触发:App 在事件里做剪贴板/Pin 时屏幕已恢复
        foreach (var w in _windows)
            NativeMethods.ShowWindow(w.Handle, Win32.SW_HIDE);

        try
        {
            Confirmed?.Invoke(result);
        }
        catch (Exception ex)
        {
            PerfLog.Log("Overlay.Confirm", $"Confirmed handler threw: {ex}");
        }
        NativeMethods.PostQuitMessage(0);
    }

    /// <summary>渲染所有窗口(同帧同一状态),全路径(合成+ULW)超 6ms 记慢帧。</summary>
    private void RenderAll()
    {
        var s = _session;
        if (s is null) return;
        foreach (var w in _windows)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                w.RenderFrame(_renderer, s.BuildModel(w));
            }
            catch (Exception ex)
            {
                PerfLog.Log("Overlay.Frame", $"display={w.Display.Index} draw FAILED: {ex.Message}");
                continue;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms > 6.0)
                PerfLog.Log("Overlay.Frame", $"SLOW full-path {ms:F2}ms display={w.Display.Index} {w.Width}x{w.Height}");
        }
    }

    /// <summary>合并消息队列里堆积的 WM_MOUSEMOVE(拖拽洪泛时不渲染中间帧)。</summary>
    private void CoalescePendingMoves()
    {
        var s = _session;
        if (s is null) return;
        while (NativeMethods.PeekMessageW(out var m, IntPtr.Zero, WM.MOUSEMOVE, WM.MOUSEMOVE, Win32.PM_REMOVE))
        {
            var w = OverlayWindow.FromHandle(m.hwnd);
            if (w is not null) s.OnMouseMove(ToVirtual(w, m.lParam));
        }
    }

    private static PxPoint ToVirtual(OverlayWindow w, IntPtr lParam)
    {
        // PMv2 线程下消息坐标 = 物理像素客户区坐标;客户区 (0,0) = 该屏 Bounds 左上角
        int x = (short)(long)lParam;
        int y = (short)((long)lParam >> 16);
        return new PxPoint(x + w.Display.Bounds.X, y + w.Display.Bounds.Y);
    }

    private IntPtr LoadedCursor(int ocrId)
    {
        if (!_cursorCache.TryGetValue(ocrId, out var h))
            _cursorCache[ocrId] = h = NativeMethods.LoadCursorW(IntPtr.Zero, ocrId);
        return h;
    }
}
