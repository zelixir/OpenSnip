using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace OpenSnip.Core;

/// <summary>
/// 超级截屏触发器(契约 §16):LL 键盘钩子在 Enabled 期间吞掉 Win 键(开始菜单不可用,Snipaste 同款已知限制);
/// Win 按住 + 左键按下 → 触发 SnipRequested(钩子线程)。默认关闭;config.ini [SuperSnip] enabled 由 App 装配。
/// <para>
/// 判定逻辑收敛为 internal Feed* 缝(行为级单测确定性驱动,不碰真实输入);
/// 生产路径:WH_KEYBOARD_LL/WH_MOUSE_LL → Feed* → 命中则吞掉并向钩子线程 marshal 触发。
/// </para>
/// </summary>
public sealed class SuperSnipTrigger : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int WmAppDispatch = 0x8000 + 0x0004; // WM_APP+4:任务队列排水

    private sealed record Job(Action Work, TaskCompletionSource<bool> Tcs);

    private readonly BlockingCollection<Job> _queue = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _stateGate = new(); // Win 状态在键盘钩子线程写、鼠标钩子线程读(同一线程时无争用)
    private readonly string _kbdClassName = "OpenSnip.SuperSnipKbd." + Guid.NewGuid().ToString("N");
    private readonly string _mouseClassName = "OpenSnip.SuperSnipMouse." + Guid.NewGuid().ToString("N");

    private static NativeMethods.LowLevelMouseProc? _sharedKbdProc;   // 键盘/鼠标钩子回调同签名
    private static NativeMethods.LowLevelMouseProc? _sharedMouseProc;
    private static NativeMethods.WndProc? _sharedWndProc;

    private nint _kbdHook;
    private nint _mouseHook;
    private nint _kbdHwnd;
    private nint _mouseHwnd;
    private uint _threadId;
    private bool _enabled;
    private bool _winDown;
    private bool _disposed;

    /// <summary>Win 按住 + 左键按下 → 触发(钩子线程)。</summary>
    public event Action? SnipRequested;

    public SuperSnipTrigger()
    {
        _thread = new Thread(ThreadProc)
        {
            Name = "OpenSnip.SuperSnip",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(3000))
        {
            throw new InvalidOperationException("SuperSnipTrigger 钩子线程 3s 未就绪。");
        }
    }

    /// <summary>启用/停用(停用即卸载双钩子并复位 Win 按住状态)。默认关闭。</summary>
    public bool Enabled
    {
        set
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(new Job(() => ApplyEnabled(value, tcs), tcs));
            PostDispatch();
            tcs.Task.Wait(2000);
        }
    }

    /// <summary>喂入键盘事件(内部测试缝/生产钩子共用):返回 true 表示该事件应被吞掉。</summary>
    internal bool FeedKey(int vk, bool down)
    {
        lock (_stateGate)
        {
            return FeedKeyCore(vk, down);
        }
    }

    private bool FeedKeyCore(int vk, bool down)
    {
        lock (_stateGate)
        {
            if (!_enabled)
            {
                return false; // 关闭:一律放行,不复位状态(未按住语义)
            }
            bool isWin = vk is VkLwin or VkRwin;
            if (!isWin)
            {
                return false;
            }
            if (down)
            {
                _winDown = true;
            }
            else
            {
                _winDown = false; // 抬起:复位按住状态
            }
            return true; // Enabled 期间 Win 全吞(按下与抬起)
        }
    }

    /// <summary>
    /// 喂入左键按下(内部测试缝/生产钩子共用):Win 按住且启用时吞掉并触发。
    /// 返回 true 表示该事件应被吞掉(事件已在调用线程触发——生产中即钩子线程,满足契约)。
    /// </summary>
    internal bool FeedLButtonDown()
    {
        Action? handlers;
        bool swallow;
        lock (_stateGate)
        {
            swallow = _enabled && _winDown;
            if (swallow)
            {
                _winDown = false; // 触发后消费按住状态:抬起事件无需再吞
            }
            handlers = SnipRequested;
        }
        if (swallow)
        {
            try
            {
                handlers?.Invoke(); // 调用线程=生产钩子线程,满足契约
            }
            catch
            {
                // 订阅方异常不影响钩子
            }
        }
        return swallow;
    }

    /// <summary>Win 按住状态(内部测试观察点)。</summary>
    internal bool WinKeyDown
    {
        get { lock (_stateGate) return _winDown; }
    }

    /// <summary>双钩子是否已安装(内部测试观察点)。</summary>
    internal bool IsHookInstalled
    {
        get { lock (_stateGate) return _kbdHook != IntPtr.Zero && _mouseHook != IntPtr.Zero; }
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
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new Job(() =>
        {
            ApplyEnabled(false, tcs);
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }, tcs));
        PostDispatch();
        if (_thread is { } t && !t.Join(2000))
        {
            PerfLog.Log("Core", "SuperSnipTrigger 钩子线程未在 2s 内退出。");
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
                if (_kbdHook == IntPtr.Zero)
                {
                    _kbdHook = NativeMethods.SetWindowsHookExW(WhKeyboardLl, _sharedKbdProc!, IntPtr.Zero, 0);
                    if (_kbdHook == IntPtr.Zero)
                    {
                        PerfLog.Log("Core", "SetWindowsHookExW(WH_KEYBOARD_LL) 失败:" + NativeMethods.LastError("SetWindowsHookExW"));
                    }
                }
                if (_mouseHook == IntPtr.Zero)
                {
                    _mouseHook = NativeMethods.SetWindowsHookExW(WhMouseLl, _sharedMouseProc!, IntPtr.Zero, 0);
                    if (_mouseHook == IntPtr.Zero)
                    {
                        PerfLog.Log("Core", "SetWindowsHookExW(WH_MOUSE_LL) 失败:" + NativeMethods.LastError("SetWindowsHookExW"));
                    }
                }
            }
            else
            {
                if (_kbdHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_kbdHook);
                    _kbdHook = IntPtr.Zero;
                }
                if (_mouseHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = IntPtr.Zero;
                }
            }
            lock (_stateGate)
            {
                _enabled = enabled && _kbdHook != IntPtr.Zero && _mouseHook != IntPtr.Zero;
                _winDown = false;
            }
            tcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            PerfLog.Log("Core", $"SuperSnip Enabled 切换异常(已吞掉):{ex.Message}");
            tcs.TrySetResult(false);
        }
    }

    private nint KbdHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && !_disposed)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool down = wParam.ToInt64() is WmKeyDown or WmSysKeyDown;
            bool up = wParam.ToInt64() is WmKeyUp or WmSysKeyUp;
            if ((down || up) && FeedKey((int)info.vkCode, down))
            {
                return 1; // 吞掉 Win
            }
        }
        return NativeMethods.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private nint MouseHookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && !_disposed && wParam.ToInt64() == WmLButtonDown)
        {
            if (FeedLButtonDown())
            {
                return 1; // 吞掉这次左键(超级截屏接管)
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void PostDispatch()
    {
        if (_kbdHwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessageW(_kbdHwnd, WmAppDispatch, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void ThreadProc()
    {
        _threadId = (uint)Environment.CurrentManagedThreadId;

        var kbdProc = new NativeMethods.LowLevelMouseProc(KbdHookProc);
        _sharedKbdProc = kbdProc; // 防回收
        var mouseProc = new NativeMethods.LowLevelMouseProc(MouseHookProc);
        _sharedMouseProc = mouseProc;
        var wndProc = new NativeMethods.WndProc(WndProc);
        _sharedWndProc = wndProc;

        // 两个消息窗口(键盘/鼠标任务排水用)
        _kbdHwnd = CreateMessageWindow(_kbdClassName, wndProc);
        _mouseHwnd = CreateMessageWindow(_mouseClassName, wndProc);
        if (_kbdHwnd == IntPtr.Zero || _mouseHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("SuperSnipTrigger 消息窗口创建失败。");
        }
        _ready.Set();

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WmAppDispatch)
            {
                DrainQueue();
                continue;
            }
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    private nint CreateMessageWindow(string className, NativeMethods.WndProc wndProc)
    {
        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = className,
        };
        if (NativeMethods.RegisterClassExW(ref wc) == 0)
        {
            return IntPtr.Zero;
        }
        return NativeMethods.CreateWindowExW(0, className, className, 0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
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
                PerfLog.Log("Core", $"SuperSnipTrigger 任务异常(已吞掉):{ex.Message}");
                job.Tcs.TrySetResult(false);
            }
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (msg == WmAppDispatch)
        {
            DrainQueue();
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
