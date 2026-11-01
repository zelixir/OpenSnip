using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenSnip.Core;

/// <summary>
/// 热角监视器(契约 §12 Core 部分):低级鼠标钩子(WH_MOUSE_LL)+ 四角驻留检测。
/// <para>
/// 角区 = 主显示器(物理像素)四角的 ZonePx 小方块;鼠标在角区静止满 Dwell 触发一次,
/// 离开角区后重新武装。CornerEntered 在钩子线程触发(经消息窗口 marshal 回钩子线程)。
/// </para>
/// </summary>
public sealed class HotCornerMonitor : IDisposable
{
    private const int ZonePx = 6;                       // 角区边长(物理 px)
    private const int WmMouseMove = 0x0200;
    private const int WmAppCornerFire = 0x8000 + 0x0002; // WM_APP+2:把事件 marshal 回钩子线程
    private const int WmAppDispatch = 0x8000 + 0x0003;   // WM_APP+3:唤醒任务队列排水
    private const int TimerPeriodMs = 25;

    private sealed record Job(Action Work, TaskCompletionSource<bool> Tcs);

    private readonly long _dwellMs;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly BlockingCollection<Job> _queue = new();
    private readonly object _stateGate = new();
    private readonly System.Threading.Timer _timer;
    private readonly string _className = "OpenSnip.HotCornerWnd." + Guid.NewGuid().ToString("N");

    // 以下状态在 钩子线程/定时器线程 间共享,一律经 _stateGate
    private PxPoint _lastPos;
    private long _lastMoveTick;
    private long _lastSeenTick;
    private int _activeCorner = -1;
    private bool _fired;
    private bool _enabled;
    private bool _hookFeed = true; // 测试缝:false 时钩子不再喂数(状态机测试确定性)

    private nint _hwnd;
    private nint _hook;
    private uint _threadId;
    private bool _disposed;

    private static NativeMethods.LowLevelMouseProc? _sharedHookProc; // 防回收(契约纪律)
    private static NativeMethods.WndProc? _sharedWndProc;

    /// <summary>鼠标在某角驻留满时长后触发。0=左上 1=右上 2=左下 3=右下(钩子线程触发)。</summary>
    public event Action<int>? CornerEntered;

    public HotCornerMonitor(TimeSpan dwell)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dwell, TimeSpan.FromMilliseconds(30));
        _dwellMs = (long)dwell.TotalMilliseconds;
        _lastPos = DisplayManager.Instance.Displays.Count > 0
            ? new PxPoint(DisplayManager.Instance.Displays[0].Bounds.X - 1000, -1000)
            : new PxPoint(-1000, -1000);

        _thread = new Thread(ThreadProc)
        {
            Name = "OpenSnip.HotCorner",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(3000))
        {
            throw new InvalidOperationException("热角钩子线程 3s 未就绪。");
        }

        // 驻留判定定时器:静止桌面没有鼠标事件,必须主动轮询
        _timer = new Timer(_ => OnTimerTick(), null, TimerPeriodMs, TimerPeriodMs);
    }

    /// <summary>启用/停用(停用即卸载钩子并复位驻留状态)。</summary>
    public bool Enabled
    {
        set
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new Job(() => ApplyEnabled(value, tcs), tcs));
            NativeMethods.PostMessageW(_hwnd, WmAppDispatch, IntPtr.Zero, IntPtr.Zero); // 唤醒钩子线程排水
            tcs.Task.Wait(2000);
        }
    }

    /// <summary>钩子是否已安装(内部测试观察点)。</summary>
    internal bool IsHookInstalled
    {
        get { lock (_stateGate) return _hook != IntPtr.Zero; }
    }

    /// <summary>测试缝:模拟定时器投递角触发消息(与 OnTimerTick 生产路径等价,经真实消息泵到钩子线程)。</summary>
    internal void DebugPostFire(int corner)
    {
        NativeMethods.PostMessageW(_hwnd, WmAppCornerFire, new IntPtr(corner), IntPtr.Zero);
    }

    /// <summary>角区判定(纯函数,物理像素):点落在主显示器四角小方块内返回角号,否则 -1。</summary>
    internal static int CornerIndexOf(PxPoint pt, PxRect bounds, int zone)
    {
        if (bounds.IsEmpty || zone <= 0)
        {
            return -1;
        }
        int x1 = bounds.X;
        int x2 = bounds.X + bounds.Width - 1;
        int y1 = bounds.Y;
        int y2 = bounds.Y + bounds.Height - 1;
        bool left = pt.X >= x1 && pt.X < x1 + zone;
        bool right = pt.X <= x2 && pt.X > x2 - zone;
        bool top = pt.Y >= y1 && pt.Y < y1 + zone;
        bool bottom = pt.Y <= y2 && pt.Y > y2 - zone;
        if (left && top)
        {
            return 0;
        }
        if (right && top)
        {
            return 1;
        }
        if (left && bottom)
        {
            return 2;
        }
        if (right && bottom)
        {
            return 3;
        }
        return -1;
    }

    /// <summary>喂入鼠标移动(内部测试缝:真实来源是低级钩子)。</summary>
    internal void FeedMove(PxPoint pt, long tick)
    {
        lock (_stateGate)
        {
            _lastPos = pt;
            _lastMoveTick = tick;
            _lastSeenTick = tick;
        }
    }

    /// <summary>暂停/恢复真实钩子喂数(状态机测试需确定性,不受物理鼠标干扰)。</summary>
    internal void SetHookFeedEnabled(bool value)
    {
        lock (_stateGate)
        {
            _hookFeed = value;
        }
    }

    /// <summary>
    /// 驻留判定一步(内部测试缝:真实来源是定时器)。
    /// 返回本步应触发的角号(未触发返回 null);触发后状态置已发射,离开角区自动复位。
    /// </summary>
    internal int? Evaluate(long nowTick)
    {
        int corner;
        long moveTick;
        bool enabled;
        lock (_stateGate)
        {
            enabled = _enabled;
            _lastSeenTick = nowTick;
            if (!enabled)
            {
                return null;
            }
            var bounds = DisplayManager.Instance.Displays.Count > 0
                ? DisplayManager.Instance.Displays[0].Bounds
                : default;
            corner = CornerIndexOf(_lastPos, bounds, ZonePx);
            if (corner == -1)
            {
                _activeCorner = -1;
                _fired = false; // 离开角区:重新武装
                return null;
            }
            _activeCorner = corner;
            moveTick = _lastMoveTick;
        }
        if (_fired)
        {
            return null;
        }
        if (nowTick - moveTick < _dwellMs)
        {
            return null; // 驻留未满
        }
        lock (_stateGate)
        {
            if (_fired || !_enabled || _activeCorner != corner)
            {
                return null;
            }
            _fired = true; // 先置位再触发,防重入
        }
        return corner;
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _timer.Dispose();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new Job(() => { ApplyEnabled(false, tcs); NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero); }, tcs));
        NativeMethods.PostMessageW(_hwnd, WmAppDispatch, IntPtr.Zero, IntPtr.Zero);
        if (_thread is { } t && !t.Join(2000))
        {
            PerfLog.Log("Core", "HotCornerMonitor 钩子线程未在 2s 内退出。");
        }
        _queue.Dispose();
        _ready.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ApplyEnabled(bool enabled, TaskCompletionSource<bool> tcs)
    {
        try
        {
            if (enabled)
            {
                if (_hook == IntPtr.Zero)
                {
                    _hook = NativeMethods.SetWindowsHookExW(
                        NativeMethods.WhMouseLl, _sharedHookProc!, IntPtr.Zero, 0);
                    if (_hook == IntPtr.Zero)
                    {
                        PerfLog.Log("Core", "SetWindowsHookExW(WH_MOUSE_LL) 失败:" + NativeMethods.LastError("SetWindowsHookExW"));
                    }
                }
            }
            else if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            lock (_stateGate)
            {
                _enabled = enabled && _hook != IntPtr.Zero;
                _fired = false;
                _activeCorner = -1;
            }
            tcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            PerfLog.Log("Core", $"HotCorner Enabled 切换异常(已吞掉):{ex.Message}");
            tcs.TrySetResult(false);
        }
    }

    private void ThreadProc()
    {
        _threadId = (uint)Environment.CurrentManagedThreadId;

        var hookProc = new NativeMethods.LowLevelMouseProc(HookProc);
        _sharedHookProc = hookProc; // 防回收
        var wndProc = new NativeMethods.WndProc(WndProc);
        _sharedWndProc = wndProc;

        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = _className,
        };
        if (NativeMethods.RegisterClassExW(ref wc) == 0)
        {
            throw new InvalidOperationException("RegisterClassExW 失败:" + NativeMethods.LastError("RegisterClassExW"));
        }
        _hwnd = NativeMethods.CreateWindowExW(0, _className, "OpenSnip.HotCorner", 0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowExW(HWND_MESSAGE) 失败:" + NativeMethods.LastError("CreateWindowExW"));
        }
        _ready.Set();

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmAppCornerFire)
            {
                int corner = (int)msg.wParam.ToInt64();
                if (corner >= 0)
                {
                    try
                    {
                        CornerEntered?.Invoke(corner); // 契约:钩子线程触发
                    }
                    catch
                    {
                        // 订阅方异常不影响钩子
                    }
                }
                continue;
            }
            if (msg.message == WmAppDispatch)
            {
                DrainQueue();
                continue;
            }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    private void DrainQueue()
    {
        while (_queue.TryTake(out var job, 0))
        {
            try
            {
                job.Work();
                job.Tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                PerfLog.Log("Core", $"HotCorner 任务异常(已吞掉):{ex.Message}");
                job.Tcs.TrySetResult(false);
            }
        }
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam.ToInt64() == WmMouseMove && !_disposed && _hookFeed)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            lock (_stateGate)
            {
                _lastPos = new PxPoint(info.pt.X, info.pt.Y);
                _lastMoveTick = now; // 仅 move 刷新驻留计时(按住不动不算)
                _lastSeenTick = now;
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (msg == WmAppCornerFire)
        {
            int corner = (int)wParam.ToInt64();
            if (corner >= 0)
            {
                try
                {
                    CornerEntered?.Invoke(corner); // 契约:钩子线程触发
                }
                catch
                {
                    // 订阅方异常不影响钩子
                }
            }
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnTimerTick()
    {
        if (_disposed || _hook == IntPtr.Zero)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        int? fire = Evaluate(now);
        if (fire is not null && _hwnd != 0)
        {
            // 事件 marshal 回钩子线程触发(契约注明"钩子线程")
            NativeMethods.PostMessageW(_hwnd, WmAppCornerFire, new IntPtr(fire.Value), IntPtr.Zero);
        }
    }
}
