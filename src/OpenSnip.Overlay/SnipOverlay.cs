// 遮罩选区主控(SPEC §2.8 契约冻结区):
//   Start() 创建内部 STA 线程 → 逐屏回调 capture 冻结画面 → 每屏建 ULW 遮罩窗口 → GetMessage 循环。
//   事件 Confirmed/Cancelled 均在内部线程触发;Close() 线程安全(视同 Cancelled)。
//   PerfLog 打点:capture 耗时 / Start()→首帧 ULW 提交 / 确认→裁剪完成(§3.2)。
using System.Collections.Concurrent;
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
    private readonly IReadOnlyList<NativeBitmap>? _historyFrames; // §9 回放:历史帧(App 拥有,Overlay 不 Dispose)
    private int _frameIndex;                                      // 0 = 当前 capture 帧
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
        : this(displays, capture, null)
    {
    }

    /// <summary>
    /// §9 回放钩子:historyFrames 为历史帧(每帧按 displays 顺序展平;缺省 null = 无回放)。
    /// 列表由调用方(App)拥有,Overlay 不负责 Dispose。
    /// </summary>
    public SnipOverlay(IReadOnlyList<DisplayInfo> displays, Func<DisplayInfo, NativeBitmap> capture,
        IReadOnlyList<NativeBitmap>? historyFrames)
    {
        _displays = displays ?? throw new ArgumentNullException(nameof(displays));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _historyFrames = historyFrames;
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

    // —————————————————— §8.5 快照导出(vdesk 无头 E2E)——————————————————
    // cookie → 完成源:PostMessage APP_SNAPSHOT 到内部线程,EncodeSnapshot 在该线程
    // 与 RenderFrame/Commit 同线程执行(无撕裂),结果经 TCS 回调用方。
    private int _snapshotCookie;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<(byte[]? Png, int W, int H)>> _snapshots = new();

    // —————————————————— §8.1 测试驱动(vdesk 无头 E2E)——————————————————
    // 与 Close() 相同的封送机制:PostMessage 到内部 UI 线程,由 HandleMessage 在
    // 既有状态机上执行并照常重绘/触发事件。遮罩未激活(窗口未建/已退出)时为 no-op。
    // 64 位打包:x,y → wParam 低/高 32 位;w,h → lParam 低/高 32 位(负坐标位模式无损)。

    /// <summary>线程安全:设置选区(虚拟桌面物理 px),等价一次拖拽完成。走既有选区状态机。</summary>
    public void PostSetRegion(int x, int y, int w, int h)
    {
        long hwnd = Interlocked.Read(ref _firstHwnd);
        if (hwnd == 0) return;
        IntPtr wParam = new IntPtr((uint)x | ((long)(uint)y << 32));
        IntPtr lParam = new IntPtr((uint)w | ((long)(uint)h << 32));
        NativeMethods.PostMessageW(new IntPtr(hwnd), WM.APP_SETREGION, wParam, lParam);
    }

    /// <summary>线程安全:等价 Enter。无选区时确认悬停窗口的既有语义保留。</summary>
    public void PostConfirm()
    {
        long hwnd = Interlocked.Read(ref _firstHwnd);
        if (hwnd == 0) return;
        NativeMethods.PostMessageW(new IntPtr(hwnd), WM.APP_CONFIRM, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>线程安全:等价 Esc(Cancelled 事件照常在内部线程触发)。</summary>
    public void PostCancel()
    {
        long hwnd = Interlocked.Read(ref _firstHwnd);
        if (hwnd == 0) return;
        NativeMethods.PostMessageW(new IntPtr(hwnd), WM.APP_CANCEL, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// §8.5 线程安全:把活动遮罩的渲染 DIB(与 ULW 提交同一内存)编码为 PNG。
    /// 与 Post* 同封送通道(APP_SNAPSHOT → 内部线程编码),同步等待 ≤5s;
    /// 无活动遮罩(窗口未建/已退出)或超时返回 null。
    /// </summary>
    public (byte[] Png, int Width, int Height)? SnapshotPng()
    {
        long hwnd = Interlocked.Read(ref _firstHwnd);
        if (hwnd == 0) return null;
        int cookie = Interlocked.Increment(ref _snapshotCookie);
        var tcs = new TaskCompletionSource<(byte[]? Png, int W, int H)>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots[cookie] = tcs;
        NativeMethods.PostMessageW(new IntPtr(hwnd), WM.APP_SNAPSHOT, new IntPtr(cookie), IntPtr.Zero);
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            _snapshots.TryRemove(cookie, out _);
            PerfLog.Log("Overlay.Snapshot", "timeout(内部线程 5s 未应答)");
            return null;
        }
        _snapshots.TryRemove(cookie, out _);
        var r = tcs.Task.Result;
        return r.Png is null ? null : (r.Png, r.W, r.H);
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

            _session = new OverlaySession(_displays, _frozen, _selfHwnds,
                d => _windows.Find(w => w.Display.Index == d.Index)?.FrozenImage);
            // §9 工具条按钮:确认/取消(等价 Enter/Esc)
            _session.Annot.ConfirmRequested += () => ConfirmAndExit();
            _session.Annot.CancelRequested += () => NativeMethods.PostQuitMessage(0);

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

                // 内容级自检:首帧后直读 DIB 抽样,全暗(<0.1% 非黑)说明渲染管线断裂
                // (如 E2E 20261031-141858:Src 遮罩把画面整像素替换成纯黑)。防无声回归。
                try
                {
                    double nonBlack = w.SampleNonBlackRatio();
                    if (nonBlack < 0.001)
                        PerfLog.Log("Overlay.FirstFrame",
                            $"WARN content all-dark display={w.Display.Index} nonBlack={nonBlack:P2} (<0.1%, 冻结画面缺失?)");
                    else
                        PerfLog.Log("Overlay.FirstFrame",
                            $"contentCheck display={w.Display.Index} nonBlack={nonBlack:P2}");
                }
                catch (Exception ex)
                {
                    PerfLog.Log("Overlay.FirstFrame", $"contentCheck FAILED display={w.Display.Index}: {ex.Message}");
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

                case WM.APP_SETREGION: // §8.1 测试驱动
                {
                    int sx = (int)(long)wParam;
                    int sy = (int)((long)wParam >> 32);
                    int sw = (int)(long)lParam;
                    int sh = (int)((long)lParam >> 32);
                    if (_session?.SetRegionExternal(new PxRect(sx, sy, sw, sh)) == true)
                        RenderAll(); // 重绘出选区边框/锚点/尺寸标签(vdesk PrintWindow 可见)
                    return true;
                }

                case WM.APP_CONFIRM: // §8.1:与 VK_RETURN / 双击完全同分支
                {
                    if (_session is { } sc && sc.Selection is null && !sc.TrySelectHover()) return true;
                    ConfirmAndExit();
                    return true;
                }

                case WM.APP_CANCEL: // §8.1:与 VK_ESCAPE 完全同分支
                    NativeMethods.PostQuitMessage(0);
                    return true;

                case WM.APP_SNAPSHOT: // §8.5 测试驱动:进程内导出渲染 DIB(wParam=cookie)
                {
                    int cookie = unchecked((int)(long)wParam);
                    if (_snapshots.TryRemove(cookie, out var tcs))
                    {
                        try
                        {
                            // 主屏窗口(E2E 单屏场景即唯一窗口),语义稳定
                            var target = _windows.Find(x => x.Display.IsPrimary) ?? _windows[0];
                            var (png, cw, ch) = target.EncodeSnapshot();
                            PerfLog.Log("Overlay.Snapshot", $"encoded {cw}x{ch} bytes={png.Length}");
                            tcs.TrySetResult((png, cw, ch));
                        }
                        catch (Exception ex)
                        {
                            PerfLog.Log("Overlay.Snapshot", $"encode FAILED: {ex.Message}");
                            tcs.TrySetResult((null, 0, 0));
                        }
                    }
                    return true;
                }

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
        bool ctrl = (NativeMethods.GetKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        bool shift = (NativeMethods.GetKeyState(Win32.VK_SHIFT) & 0x8000) != 0;
        var sess = _session;

        // §9 标注键(Del/Ctrl+Z/Ctrl+Y/Ctrl+Shift+Z/空格)——仅标注相位消费
        if (sess is not null && sess.Phase == SnipPhase.Selected && sess.Annot.HandleKey(vk, ctrl, shift))
        {
            RenderAll();
            return;
        }

        // §9 回放:逗号/句号循环切换冻结帧
        if (vk == Win32.VK_OEM_COMMA) { SwitchFrame(-1); return; }
        if (vk == Win32.VK_OEM_PERIOD) { SwitchFrame(1); return; }

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

    /// <summary>
    /// §9 回放:逗号/句号循环切换冻结帧。
    /// 帧 0 = 当前 capture 帧;1..n = historyFrames(按 displays 顺序展平,第 k 帧第 d 屏
    /// = historyFrames[(k-1)*displays.Count + d])。标注保留(与帧无关的视觉叠加)。
    /// </summary>
    private void SwitchFrame(int dir)
    {
        var frames = _historyFrames;
        if (frames is not { Count: > 0 }) return;
        int perFrame = Math.Max(1, _displays.Count);
        int total = 1 + (frames.Count + perFrame - 1) / perFrame;
        _frameIndex = ((_frameIndex + dir) % total + total) % total;
        foreach (var w in _windows)
        {
            try
            {
                if (_frameIndex == 0)
                {
                    if (_frozen.TryGetValue(w.Display.Index, out var cap) && cap is not null)
                        w.SetFrozen(cap); // 回当前帧(重建 premul 转存,一次性成本可接受)
                }
                else
                {
                    int idx = (_frameIndex - 1) * perFrame + w.Display.Index;
                    if (idx < frames.Count)
                    {
                        var fb = frames[idx];
                        EnsureOpaque(fb);
                        w.SetFrozen(fb);
                    }
                }
            }
            catch (Exception ex)
            {
                PerfLog.Log("Overlay.Frame", $"switchFrame display={w.Display.Index} FAILED: {ex.Message}");
            }
        }
        _session?.Annot.OnFrameSwitched(_frameIndex);
        RenderAll();
        PerfLog.Log("Overlay.Frame", $"historyFrame index={_frameIndex}/{total}");
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
