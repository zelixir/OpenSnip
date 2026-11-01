using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenSnip.Core;
using SkiaSharp;

namespace OpenSnip.Pin;

/// <summary>
/// 贴图窗口(契约 §2.9/§3.3):WS_POPUP + WS_EX_LAYERED 自管 HWND,
/// 内容经 Skia 渲染后用 UpdateLayeredWindow 提交。
/// 支持:拖动移动、滚轮缩放(1.1 步进、指针锚点)、Ctrl+滚轮透明度、双击关闭、
/// 右键菜单(置顶/鼠标穿透/关闭)、屏幕贴边吸附(&lt;20×DPI)。
/// HWND 仅在贴图线程操作;公开成员线程安全(内部序列化到贴图线程)。
/// 所有权:本窗口持有传入的 <see cref="NativeBitmap"/>,Close 后 Dispose(契约 §3.3)。
/// </summary>
public sealed class PinWindow : IDisposable
{
    /// <summary>滚轮缩放步进系数(契约 §3.3:1.1 步进)。</summary>
    internal const double WheelStepFactor = 1.1;

    /// <summary>缩放上下限(防止把图缩没/撑爆显存)。</summary>
    internal const double MinScale = 0.05;
    internal const double MaxScale = 32.0;

    /// <summary>贴边吸附阈值:距离屏幕边 &lt;20×DPI(物理 px)时吸附(契约 §3.3)。</summary>
    internal const double SnapThresholdDip = 20.0;

    /// <summary>Ctrl+滚轮每格透明度步进(契约范围 10~100;步长自定 5,见交付报告)。</summary>
    internal const byte OpacityWheelStep = 5;
    internal const byte MinOpacity = 10;
    internal const byte MaxOpacity = 100;

    /// <summary>窗口类名。</summary>
    internal const string WindowClassName = "OpenSnip.Pin";

    /// <summary>静态持有窗口过程委托防 GC(契约 §3.1 同款纪律)。</summary>
    private static readonly NativeMethods.WndProcDelegate WndProcStatic = PinWindowWndProc;

    /// <summary>HWND → 实例映射(静态 WndProc 的分发依据;仅贴图线程增删)。</summary>
    private static readonly ConcurrentDictionary<IntPtr, PinWindow> WindowMap = new();

    /// <summary>窗口类已注册标记(进程级幂等)。</summary>
    private static int _windowClassRegistered;

    // ===== 状态 =====
    private readonly object _gate = new();
    private readonly PinDispatcher _dispatcher;
    private readonly NativeBitmap _image;      // 所有权:本窗口持有,关闭时 Dispose(契约 §3.3)
    private PxRect _rect;
    private double _scale = 1.0;
    private byte _opacity = MaxOpacity;
    private bool _topmost = true;
    private bool _clickThrough;

    private IntPtr _hwnd;
    private PinRenderTarget? _target;
    private SKBitmap? _baseBitmap;     // 原尺寸预乘副本(重采样统一源)
    private SKBitmap? _scaledBitmap;   // 当前缩放尺寸的内容位图(ULW 提交物)
    private GCHandle _selfHandle;
    private int _destroyed;            // 0=存活,1=已销毁(防重入)
    private bool _lastCommitOk;        // 最近一次 ULW 提交结果(自测/诊断)

    // 拖拽状态(仅贴图线程访问)
    private bool _dragging;
    private PxPoint _dragStartCursor;
    private PxPoint _dragStartWindowPos;

    // ===== 背景模式状态(契约 §11;仅贴图线程访问)=====
    private PinBackground _background = PinBackground.Transparent;   // 默认透明 = M1 行为,零变化
    private SKBitmap? _backgroundBitmap;      // 伪透明捕获快照(与窗口同尺寸,预乘 BGRA)
    private SKBitmap? _checkerboardBitmap;    // 棋盘缓存(尺寸/深浅不匹配时重建)
    private PinBackground _checkerboardKind;  // 棋盘缓存对应的深浅
    private bool _bgRefreshPending;           // WM_TIMER 防抖挂起
    private bool _refreshingBackground;       // 重截进行中(瞬隐期间不再调度/重入)
    private const int BgRefreshTimerId = 1;
    private const int BgRefreshDelayMs = 150;

    /// <summary>窗口关闭后在贴图线程触发;触发时窗口与非托管资源已全部释放。</summary>
    public event Action<PinWindow>? Closed;

    internal PinWindow(PinDispatcher dispatcher, NativeBitmap image, PxRect initialRect)
    {
        _dispatcher = dispatcher;
        _image = image;

        // 初始缩放:以 initialRect.Width 为基准等比缩放(initialRect 为显示位置与尺寸,契约 §2.9)。
        // 高按原图比例推算;若 App 给出的矩形宽高比与原图不符,窗口高 ≠ initialRect.Height(见交付报告)。
        if (initialRect.Width > 0)
        {
            _scale = Math.Clamp((double)initialRect.Width / image.Width, MinScale, MaxScale);
        }
        int w = Math.Max(1, (int)Math.Round(image.Width * _scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * _scale));
        _rect = new PxRect(initialRect.X, initialRect.Y, w, h);

        _selfHandle = GCHandle.Alloc(this);
        try
        {
            EnsureWindowClass();
            _hwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW,
                WindowClassName,
                WindowClassName,
                NativeMethods.WS_POPUP,
                _rect.X, _rect.Y, w, h,
                IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), GCHandle.ToIntPtr(_selfHandle));
            if (_hwnd == IntPtr.Zero)
            {
                throw new OutOfMemoryException($"CreateWindowExW 失败,Win32Error={Marshal.GetLastWin32Error()}");
            }
            WindowMap[_hwnd] = this;   // WM_NCCREATE 已插入;此处幂等兜底

            _target = new PinRenderTarget();
            BuildBitmaps();
            Commit();   // 首帧 ULW 提交;失败不致命(窗口暂空白),后续交互重试
            _ = NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }
        catch
        {
            if (_hwnd == IntPtr.Zero)
            {
                // 窗口尚未建成,WM_NCDESTROY 清理链不会走:手动完成清理(含位图所有权)
                if (_selfHandle.IsAllocated)
                {
                    _selfHandle.Free();
                }
                _image.Dispose();
                throw;
            }
            // 已有窗口:走 DestroyWindow → WM_NCDESTROY → OnNcDestroyed 全量清理(含位图与 GCHandle)
            _ = NativeMethods.DestroyWindow(_hwnd);
            throw;
        }
    }

    // ==================== 公开契约(§2.9 冻结签名) ====================

    /// <summary>当前窗口位置与尺寸(虚拟桌面物理 px)。</summary>
    public PxRect CurrentRect
    {
        get { lock (_gate) { return _rect; } }
    }

    /// <summary>置顶开关(默认 true;同步生效)。</summary>
    public bool Topmost
    {
        set => _dispatcher.Invoke(() => ApplyTopmost(value));
    }

    /// <summary>鼠标穿透开关(SetWindowLongW 切 WS_EX_TRANSPARENT,契约 §3.3;同步生效)。</summary>
    public bool ClickThrough
    {
        set => _dispatcher.Invoke(() => ApplyClickThrough(value));
    }

    /// <summary>不透明度百分比,10~100(契约 §2.9;低于 10 按 10 处理,同步生效)。</summary>
    public byte OpacityPercent
    {
        set => _dispatcher.Invoke(() => ApplyOpacity(value));
    }

    /// <summary>
    /// 背景模式(契约 §11;默认 Transparent = M1 行为;同步生效)。
    /// 切到伪透明:立即瞬隐一帧重截当前背后的屏幕快照;之后位置/尺寸变化经 150ms 防抖自动重截。
    /// 回退(浅棋盘 + PerfLog):窗口跨屏或出屏、截屏异常、捕获到纯黑(隐藏桌面,SPEC §8.4)。
    /// </summary>
    public PinBackground Background
    {
        set => _dispatcher.Invoke(() => ApplyBackground(value));
    }

    /// <summary>关闭并销毁窗口;幂等。Closed 事件随后在贴图线程触发。</summary>
    public void Close()
    {
        try
        {
            _dispatcher.Invoke(BeginDestroy);
        }
        catch (InvalidOperationException)
        {
            // 贴图线程已退出:窗口随线程销毁,Close 视为已完成
        }
    }

    /// <summary>等价 <see cref="Close"/>(契约 §2.9 的 IDisposable 语义)。</summary>
    public void Dispose() => Close();

    // ==================== 内部诊断/自测 ====================

    /// <summary>最近一次 ULW 提交是否成功(自测/诊断用)。</summary>
    internal bool LastCommitOk => _lastCommitOk;

    /// <summary>窗口是否已销毁(自测用)。</summary>
    internal bool IsDestroyed => Volatile.Read(ref _destroyed) == 1;

    // ==================== 窗口类与 WndProc ====================

    private static void EnsureWindowClass()
    {
        if (Interlocked.CompareExchange(ref _windowClassRegistered, 1, 0) != 0)
        {
            return;
        }
        var wc = new NativeMethods.WNDCLASSEX
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            Style = NativeMethods.CS_DBLCLKS,   // 双击关闭需要 WM_LBUTTONDBLCLK
            LpfnWndProc = WndProcStatic,
            HInstance = NativeMethods.GetModuleHandleW(null),
            HCursor = NativeMethods.LoadCursorW(IntPtr.Zero, NativeMethods.IDC_ARROW),
            LpszClassName = WindowClassName,
        };
        if (NativeMethods.RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != NativeMethods.ERROR_CLASS_ALREADY_EXISTS)
            {
                Volatile.Write(ref _windowClassRegistered, 0);   // 失败复位,允许下次重试
                throw new OutOfMemoryException($"RegisterClassExW 失败,Win32Error={err}");
            }
        }
    }

    private static IntPtr PinWindowWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // WM_NCCREATE:从创建参数(GCHandle)取回实例,建立 HWND → 实例映射
        if (msg == NativeMethods.WM_NCCREATE)
        {
            try
            {
                if (GCHandle.FromIntPtr(lParam).Target is PinWindow win)
                {
                    WindowMap[hWnd] = win;
                }
            }
            catch
            {
                // lParam 非本类库创建路径的 GCHandle:忽略
            }
            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);   // 必须透传以存储窗口名
        }

        if (WindowMap.TryGetValue(hWnd, out var window))
        {
            try
            {
                return window.HandleMessage(hWnd, msg, wParam, lParam);
            }
            catch
            {
                // 单条消息异常(渲染/重采样失败等)不冲垮消息循环
                return 0;
            }
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_CLOSE:
                BeginDestroy();
                return 0;   // 吞掉 WM_CLOSE,自管销毁

            case NativeMethods.WM_NCDESTROY:
                return OnNcDestroyed(hWnd);

            case NativeMethods.WM_ERASEBKGND:
                return 1;   // ULW 窗口无背景可擦,防闪烁

            case NativeMethods.WM_LBUTTONDOWN:
                OnDragStart(hWnd, lParam);
                return 0;

            case NativeMethods.WM_MOUSEMOVE:
                OnMouseMove(wParam, lParam);
                return 0;

            case NativeMethods.WM_LBUTTONUP:
                OnDragEnd();
                return 0;

            case NativeMethods.WM_LBUTTONDBLCLK:
                BeginDestroy();   // 双击关闭(契约 §3.3)
                return 0;

            case NativeMethods.WM_RBUTTONUP:
                ShowContextMenu(hWnd);
                return 0;

            case NativeMethods.WM_MOUSEWHEEL:
                OnWheel(wParam);
                return 0;

            case NativeMethods.WM_TIMER:
                if (wParam.ToInt64() == BgRefreshTimerId)
                {
                    // WM_TIMER 仅在队列空闲时投递:天然"静止后才刷新",拖动/连滚不频闪
                    _ = NativeMethods.KillTimer(_hwnd, BgRefreshTimerId);
                    _bgRefreshPending = false;
                    RefreshPseudoBackgroundNow();
                }
                return 0;

            // WM_DPICHANGED(PMv2 跨屏拖动):v1 忽略,保持物理尺寸(贴图为物理像素快照,与 Snipaste 一致)
            default:
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    // ==================== 交互:拖动 ====================

    private void OnDragStart(IntPtr hWnd, IntPtr lParam)
    {
        _ = NativeMethods.SetCapture(hWnd);
        var cur = PointFromLParam(lParam);
        _dragging = true;
        _dragStartCursor = cur;
        lock (_gate)
        {
            _dragStartWindowPos = new PxPoint(_rect.X, _rect.Y);
        }
    }

    private void OnMouseMove(IntPtr wParam, IntPtr lParam)
    {
        if (!_dragging || (unchecked((int)(long)wParam) & NativeMethods.MK_LBUTTON) == 0)
        {
            return;
        }
        var cur = PointFromLParam(lParam);
        int nx = _dragStartWindowPos.X + (cur.X - _dragStartCursor.X);
        int ny = _dragStartWindowPos.Y + (cur.Y - _dragStartCursor.Y);
        (nx, ny) = SnapToScreenEdges(nx, ny, _rect.Width, _rect.Height, cur);
        SetWindowRect(nx, ny, _rect.Width, _rect.Height, sizeChanged: false);
    }

    private void OnDragEnd()
    {
        _dragging = false;
        _ = NativeMethods.ReleaseCapture();
    }

    /// <summary>lParam 打包的屏幕坐标(多屏下可能为负,按有符号 16 位解包)。</summary>
    private static PxPoint PointFromLParam(IntPtr lParam)
    {
        int v = unchecked((int)(long)lParam);
        return new PxPoint((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    private void SetWindowRect(int x, int y, int w, int h, bool sizeChanged)
    {
        uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER
            | (sizeChanged ? 0u : NativeMethods.SWP_NOSIZE);
        if (NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, flags))
        {
            lock (_gate)
            {
                _rect = new PxRect(x, y, w, h);
            }
            ScheduleBackgroundRefresh();   // 位置/尺寸变化:伪透明背景缓存失效(契约 §11"移动/缩放时重截")
        }
    }

    // ==================== 交互:滚轮缩放 / 透明度 ====================

    private void OnWheel(IntPtr wParam)
    {
        int wp = unchecked((int)(long)wParam);
        int delta = (short)((wp >> 16) & 0xFFFF);
        bool ctrl = (wp & NativeMethods.MK_CONTROL) != 0;
        if (!NativeMethods.GetCursorPos(out var cur))
        {
            return;
        }

        if (ctrl)
        {
            // Ctrl+滚轮:透明度 ±5,范围 [10,100](契约 §2.9/§3.3)
            int step = delta > 0 ? OpacityWheelStep : -OpacityWheelStep;
            ApplyOpacity((byte)Math.Clamp(_opacity + step, MinOpacity, MaxOpacity));
        }
        else
        {
            // 滚轮:1.1 步进缩放,指针为锚点(指针下的内容点保持屏幕位置不动)
            double newScale = Math.Clamp(_scale * Math.Pow(WheelStepFactor, delta / 120.0), MinScale, MaxScale);
            if (newScale != _scale)
            {
                ApplyScale(newScale, new PxPoint(cur.X, cur.Y));
            }
        }
    }

    /// <summary>按新缩放重建内容,并以 anchor(屏幕物理坐标)为锚点调整窗口位置。</summary>
    private void ApplyScale(double newScale, PxPoint anchor)
    {
        if (_baseBitmap is null)
        {
            return;
        }
        int newW = Math.Max(1, (int)Math.Round(_image.Width * newScale));
        int newH = Math.Max(1, (int)Math.Round(_image.Height * newScale));

        int curX, curY, curW, curH;
        lock (_gate)
        {
            curX = _rect.X; curY = _rect.Y; curW = _rect.Width; curH = _rect.Height;
        }

        double fx = curW > 0 ? (anchor.X - curX) / (double)curW : 0.0;
        double fy = curH > 0 ? (anchor.Y - curY) / (double)curH : 0.0;
        int nx = (int)Math.Round(anchor.X - fx * newW);
        int ny = (int)Math.Round(anchor.Y - fy * newH);
        (nx, ny) = SnapToScreenEdges(nx, ny, newW, newH, anchor);

        var resized = PinRenderer.ResizePremul(_baseBitmap, newW, newH);
        if (resized is null)
        {
            return;   // 重采样失败:保持现状,下一格滚轮再试
        }
        _scaledBitmap?.Dispose();
        _scaledBitmap = resized;
        _scale = newScale;

        SetWindowRect(nx, ny, newW, newH, sizeChanged: true);
        Commit();
    }

    private void ApplyOpacity(byte value)
    {
        byte v = Math.Clamp(value, MinOpacity, MaxOpacity);
        if (v == _opacity)
        {
            return;
        }
        _opacity = v;
        Commit();   // 仅重提交(颜色矩阵衰减),不重采样
    }

    // ==================== 贴边吸附 ====================

    /// <summary>
    /// 贴边吸附:窗口边距离所在显示器边缘 &lt;20×DPI(物理 px)时吸附到该边(契约 §3.3)。
    /// 取不到显示器(Core 枚举异常/窗口在屏幕外)时原样返回,不影响主流程。
    /// </summary>
    private (int X, int Y) SnapToScreenEdges(int x, int y, int w, int h, PxPoint prefer)
    {
        var display = TryGetDisplayAt(new PxPoint(x + w / 2, y + h / 2)) ?? TryGetDisplayAt(prefer);
        if (display is null)
        {
            return (x, y);
        }

        PxRect b = display.Bounds;
        double threshold = SnapThresholdDip * display.DpiScale;
        int left = b.X, top = b.Y, right = b.X + b.Width, bottom = b.Y + b.Height;

        if (Math.Abs(x - left) < threshold)
        {
            x = left;
        }
        else if (Math.Abs(right - (x + w)) < threshold)
        {
            x = right - w;
        }
        if (Math.Abs(y - top) < threshold)
        {
            y = top;
        }
        else if (Math.Abs(bottom - (y + h)) < threshold)
        {
            y = bottom - h;
        }
        return (x, y);
    }

    private static DisplayInfo? TryGetDisplayAt(PxPoint pt)
    {
        try
        {
            return DisplayManager.Instance.GetDisplayAt(pt);
        }
        catch
        {
            return null;   // Core 侧枚举失败:放弃吸附,不阻塞交互
        }
    }

    // ==================== 右键菜单 ====================

    /// <summary>右键菜单:置顶开关 / 鼠标穿透开关 / 关闭(契约 §3.3 v1;"保存为 PNG"留 M2)。</summary>
    private void ShowContextMenu(IntPtr hWnd)
    {
        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }
        try
        {
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)NativeMethods.MENU_ID_TOPMOST, "置顶");
            _ = NativeMethods.CheckMenuItem(menu, NativeMethods.MENU_ID_TOPMOST, _topmost ? NativeMethods.MF_CHECKED : 0);
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)NativeMethods.MENU_ID_CLICKTHROUGH, "鼠标穿透");
            _ = NativeMethods.CheckMenuItem(menu, NativeMethods.MENU_ID_CLICKTHROUGH, _clickThrough ? NativeMethods.MF_CHECKED : 0);

            // 背景模式子菜单(契约 §11):当前模式打勾;子菜单由父菜单 DestroyMenu 一并销毁
            IntPtr bgMenu = NativeMethods.CreatePopupMenu();
            _ = NativeMethods.AppendMenuW(bgMenu, NativeMethods.MF_STRING, (UIntPtr)(NativeMethods.MENU_ID_BG_FIRST + 0u), "透明(默认)");
            _ = NativeMethods.AppendMenuW(bgMenu, NativeMethods.MF_STRING, (UIntPtr)(NativeMethods.MENU_ID_BG_FIRST + 1u), "伪透明");
            _ = NativeMethods.AppendMenuW(bgMenu, NativeMethods.MF_STRING, (UIntPtr)(NativeMethods.MENU_ID_BG_FIRST + 2u), "深色棋盘");
            _ = NativeMethods.AppendMenuW(bgMenu, NativeMethods.MF_STRING, (UIntPtr)(NativeMethods.MENU_ID_BG_FIRST + 3u), "浅色棋盘");
            _ = NativeMethods.CheckMenuItem(bgMenu, NativeMethods.MENU_ID_BG_FIRST + (uint)_background, NativeMethods.MF_CHECKED);
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_POPUP, (UIntPtr)bgMenu, "背景模式");
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            _ = NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)NativeMethods.MENU_ID_CLOSE, "关闭");

            // TrackPopupMenu 经典配对:先置前台,否则点击菜单外区域菜单不消失
            _ = NativeMethods.SetForegroundWindow(hWnd);
            if (!NativeMethods.GetCursorPos(out var pt))
            {
                lock (_gate)
                {
                    pt = new NativeMethods.POINT { X = _rect.X + _rect.Width / 2, Y = _rect.Y + _rect.Height / 2 };
                }
            }
            uint cmd = NativeMethods.TrackPopupMenuEx(
                menu, NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON, pt.X, pt.Y, hWnd, IntPtr.Zero);
            _ = NativeMethods.PostMessageW(hWnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd >= NativeMethods.MENU_ID_BG_FIRST && cmd < NativeMethods.MENU_ID_BG_FIRST + 4u)
            {
                ApplyBackground((PinBackground)(int)(cmd - NativeMethods.MENU_ID_BG_FIRST));
            }
            else
            {
                switch (cmd)
                {
                    case NativeMethods.MENU_ID_TOPMOST:
                        ApplyTopmost(!_topmost);
                        break;
                    case NativeMethods.MENU_ID_CLICKTHROUGH:
                        ApplyClickThrough(!_clickThrough);
                        break;
                    case NativeMethods.MENU_ID_CLOSE:
                        BeginDestroy();
                        break;
                }
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    // ==================== 置顶 / 穿透 ====================

    private void ApplyTopmost(bool value)
    {
        if (_hwnd == IntPtr.Zero || value == _topmost)
        {
            return;
        }
        var insertAfter = value ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
        if (NativeMethods.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE))
        {
            _topmost = value;
        }
    }

    private void ApplyClickThrough(bool value)
    {
        if (_hwnd == IntPtr.Zero || value == _clickThrough)
        {
            return;
        }
        int ex = NativeMethods.GetWindowLongW(_hwnd, NativeMethods.GWL_EXSTYLE);
        // 合并位常量(0x08000020 < int.MaxValue,unchecked 转换安全),避免 int/uint 混算提升为 long
        const int transparentNoActivate = unchecked((int)(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE));
        int next = value
            ? ex | transparentNoActivate   // 穿透时同时不抢焦点
            : ex & ~transparentNoActivate;
        _ = NativeMethods.SetWindowLongW(_hwnd, NativeMethods.GWL_EXSTYLE, next);
        _clickThrough = value;
    }

    // ==================== 背景模式(契约 §11) ====================

    private void ApplyBackground(PinBackground value)
    {
        if (_hwnd == IntPtr.Zero || value == _background)
        {
            return;
        }
        _background = value;
        if (value == PinBackground.PseudoTransparent)
        {
            RefreshPseudoBackgroundNow();   // 立即重截(内部含恢复提交)
        }
        else
        {
            DisposeBackgroundBitmaps();     // Transparent 无背景层;棋盘按需重建
            Commit();
        }
    }

    /// <summary>
    /// 伪透明重截:窗口瞬隐一帧(ULW 全透明)→ 等 DWM 合成 → 截屏窗口所在屏 →
    /// CopyRegionFrom 裁出窗口区域 → 恢复显示(新背景 + 内容)。
    /// 前置:窗口完全落在单个显示器内(跨屏/出屏回退);失败回退浅棋盘 + PerfLog(契约 §11/§8.4)。
    /// </summary>
    private void RefreshPseudoBackgroundNow()
    {
        if (_background != PinBackground.PseudoTransparent || _hwnd == IntPtr.Zero
            || _target is null || _scaledBitmap is null || _refreshingBackground)
        {
            return;
        }
        _refreshingBackground = true;
        try
        {
            int x, y, w, h;
            lock (_gate)
            {
                x = _rect.X;
                y = _rect.Y;
                w = _rect.Width;
                h = _rect.Height;
            }
            var display = TryGetDisplayAt(new PxPoint(x + w / 2, y + h / 2));
            // 窗口必须完全落在单个显示器内(跨屏/出屏 → 回退;同时完成可空收窄)
            if (display is null
                || display.Bounds.X > x || display.Bounds.Y > y
                || x + w > display.Bounds.X + display.Bounds.Width
                || y + h > display.Bounds.Y + display.Bounds.Height)
            {
                FallbackBackground("offscreen-or-cross-display", x, y, w, h);
                return;
            }

            // 瞬隐:贴图先从屏幕上"拿开",截屏才不会把贴图自己截进背景
            if (_lastCommitOk)
            {
                _ = _target.CommitTransparentFrame(_hwnd, x, y);
            }
            try
            {
                Thread.Sleep(24);   // 等 DWM 完成一帧合成,确保瞬隐帧已上屏
                var svc = PinManager.EnsureCaptureService();
                if (svc is null)
                {
                    FallbackBackground("capture-service-unavailable", x, y, w, h);
                    return;
                }
                using var cap = svc.Capture(display);
                if (IsCapturedContentBlank(cap))
                {
                    // 纯黑 = 隐藏桌面空桌特征(SPEC §8.4:BitBlt 在空桌面产出"合法黑")
                    FallbackBackground("blank-capture", x, y, w, h);
                    return;
                }
                var bg = NativeBitmap.Allocate(w, h);
                bg.CopyRegionFrom(cap, x - display.Bounds.X, y - display.Bounds.Y);
                SKBitmap premul;
                using (var wrap = BitmapConversion.WrapAsSkia(bg))
                {
                    premul = PinRenderer.BuildPremulBase(wrap);
                }
                bg.Dispose();   // 预乘副本已建,本次捕获的 NativeBitmap 所有权在此结束
                _backgroundBitmap?.Dispose();
                _backgroundBitmap = premul;
            }
            catch (Exception ex)
            {
                FallbackBackground("capture-failed: " + ex.Message, x, y, w, h);
            }
        }
        finally
        {
            Commit();   // 恢复显示:成功=新背景;失败=回退层(浅棋盘)
            _refreshingBackground = false;
        }
    }

    /// <summary>伪透明回退:丢弃捕获缓存(渲染层解析为浅棋盘)+ PerfLog 打点;不提交,由调用方统一恢复。</summary>
    private void FallbackBackground(string reason, int x, int y, int w, int h)
    {
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
        PerfLog.Log("Pin", "bg-fallback reason=" + reason + " rect=" + x + "," + y + " " + w + "x" + h);
    }

    /// <summary>位置/尺寸变化后调度伪透明重截(150ms 防抖:拖动/连滚不频闪,静止后一次截屏)。</summary>
    private void ScheduleBackgroundRefresh()
    {
        if (_background != PinBackground.PseudoTransparent || _hwnd == IntPtr.Zero
            || _bgRefreshPending || _refreshingBackground)
        {
            return;
        }
        _bgRefreshPending = true;
        _ = NativeMethods.SetTimer(_hwnd, BgRefreshTimerId, BgRefreshDelayMs, IntPtr.Zero);
    }

    /// <summary>Commit 的背景层解析:伪透明=快照(缺/失效→浅棋盘回退);棋盘模式=棋盘;Transparent=null(逐像素 alpha)。</summary>
    private SKBitmap? GetBackgroundLayerForRender(int w, int h)
    {
        switch (_background)
        {
            case PinBackground.PseudoTransparent:
                return _backgroundBitmap ?? GetCheckerboard(dark: false, w, h);
            case PinBackground.DarkCheckerboard:
                return GetCheckerboard(dark: true, w, h);
            case PinBackground.LightCheckerboard:
                return GetCheckerboard(dark: false, w, h);
            default:
                return null;
        }
    }

    private SKBitmap GetCheckerboard(bool dark, int w, int h)
    {
        var kind = dark ? PinBackground.DarkCheckerboard : PinBackground.LightCheckerboard;
        if (_checkerboardBitmap is null || _checkerboardKind != kind
            || _checkerboardBitmap.Width != w || _checkerboardBitmap.Height != h)
        {
            _checkerboardBitmap?.Dispose();
            float dpi = TryGetDisplayAt(new PxPoint(_rect.X + w / 2, _rect.Y + h / 2))?.DpiScale ?? 1f;
            _checkerboardBitmap = PinRenderer.BuildCheckerboard(dark, w, h, dpi);
            _checkerboardKind = kind;
        }
        return _checkerboardBitmap;
    }

    /// <summary>捕获内容是否纯黑(24×24 网格采样)。真实桌面几乎不可能全黑;隐藏桌面 BitBlt 恒黑(SPEC §8.4)。</summary>
    private static unsafe bool IsCapturedContentBlank(NativeBitmap cap)
    {
        const int grid = 24;
        byte* p = (byte*)cap.Scan0;
        for (int gy = 0; gy < grid; gy++)
        {
            int y = (int)((long)gy * (cap.Height - 1) / (grid - 1));
            byte* row = p + (nint)((long)y * cap.Stride);
            for (int gx = 0; gx < grid; gx++)
            {
                int x = (int)((long)gx * (cap.Width - 1) / (grid - 1));
                int off = x * 4;
                if (row[off] != 0 || row[off + 1] != 0 || row[off + 2] != 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void DisposeBackgroundBitmaps()
    {
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
        _checkerboardBitmap?.Dispose();
        _checkerboardBitmap = null;
    }

    /// <summary>存活句柄(已销毁返回 IntPtr.Zero;供虚拟桌面查询等跨线程使用)。</summary>
    internal IntPtr GetLiveHwnd() => Volatile.Read(ref _destroyed) == 1 ? IntPtr.Zero : _hwnd;

    // ==================== 渲染管线 ====================

    /// <summary>构建基准(原尺寸预乘)位图与当前缩放位图;仅构造期在贴图线程调用一次。</summary>
    private void BuildBitmaps()
    {
        // 契约 §2.2:WrapAsSkia 零拷贝包装;用完即弃 SKBitmap,NativeBitmap 留存到窗口关闭
        using var wrapped = BitmapConversion.WrapAsSkia(_image);
        _baseBitmap = PinRenderer.BuildPremulBase(wrapped);

        int w = Math.Max(1, _rect.Width);
        int h = Math.Max(1, _rect.Height);
        _scaledBitmap = PinRenderer.ResizePremul(_baseBitmap, w, h)
            ?? throw new OutOfMemoryException("贴图初始重采样失败");
    }

    /// <summary>把当前内容(含背景层与透明度)ULW 提交到屏幕;仅贴图线程调用。</summary>
    private void Commit()
    {
        if (_hwnd == IntPtr.Zero || _target is null || _scaledBitmap is null)
        {
            return;
        }
        int x, y, w, h;
        lock (_gate)
        {
            x = _rect.X;
            y = _rect.Y;
            w = _rect.Width;
            h = _rect.Height;
        }
        var background = GetBackgroundLayerForRender(w, h);
        _lastCommitOk = _target.Commit(_hwnd, x, y, _scaledBitmap, _opacity, background);
    }

    // ==================== 销毁与清理 ====================

    /// <summary>发起销毁(幂等;仅贴图线程)。</summary>
    private void BeginDestroy()
    {
        if (Volatile.Read(ref _destroyed) == 1)
        {
            return;
        }
        if (_hwnd != IntPtr.Zero)
        {
            // 触发 WM_DESTROY → WM_NCDESTROY → OnNcDestroyed 同步清理链
            _ = NativeMethods.DestroyWindow(_hwnd);
        }
    }

    /// <summary>
    /// WM_NCDESTROY:全量清理(贴图线程)。
    /// 释放顺序:先 Skia 位图与 DIB 目标,后 NativeBitmap(契约 §2.2/§3.3),
    /// 随后移出管理列表并在贴图线程触发 Closed。
    /// </summary>
    private IntPtr OnNcDestroyed(IntPtr hWnd)
    {
        if (Interlocked.Exchange(ref _destroyed, 1) == 1)
        {
            return IntPtr.Zero;
        }

        _ = WindowMap.TryRemove(hWnd, out _);
        _ = NativeMethods.KillTimer(hWnd, BgRefreshTimerId);
        _bgRefreshPending = false;
        _hwnd = IntPtr.Zero;
        _dragging = false;

        _scaledBitmap?.Dispose();
        _scaledBitmap = null;
        _baseBitmap?.Dispose();
        _baseBitmap = null;
        _target?.Dispose();
        _target = null;
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
        _checkerboardBitmap?.Dispose();
        _checkerboardBitmap = null;
        _image.Dispose();   // 位图所有权:窗口关闭即释放(契约 §3.3)
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }

        PinManager.RemoveWindow(this);
        try
        {
            Closed?.Invoke(this);
        }
        catch
        {
            // 订阅方异常不影响销毁流程
        }
        return IntPtr.Zero;
    }
}
