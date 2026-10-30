using System.Collections.Concurrent;

namespace OpenSnip.Core;

/// <summary>
/// 全局热键管理:独占一个 STA 消息窗口线程(HWND_MESSAGE),WM_HOTKEY 在该线程触发事件。
/// Register/Unregister 编组到热键线程执行(RegisterHotKey 必须与创建窗口同线程)。
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WmAppDispatch = 0x8000 + 0x0001; // WM_APP+1:唤醒队列排水

    // WndProc 委托存静态字段防 GC(契约 §3.1 纪律)
    private static NativeMethods.WndProc? _sharedWndProc;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ConcurrentQueue<(TaskCompletionSource<bool> Tcs, Func<bool> Work)> _queue = new();
    private readonly string _className = "OpenSnip.HotkeyWnd." + Guid.NewGuid().ToString("N");
    private readonly Dictionary<int, (uint Mods, uint Vk)> _registered = new();
    private readonly object _gate = new();
    private nint _hwnd;
    private uint _threadId;
    private bool _disposed;

    /// <summary>热键触发(在热键线程触发,订阅方自行调度回 UI 线程)。</summary>
    public event Action<int>? HotkeyPressed;

    public GlobalHotkeyManager()
    {
        _thread = new Thread(ThreadProc)
        {
            Name = "OpenSnip.Hotkey",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(10000))
        {
            throw new InvalidOperationException("热键窗口线程 10s 未就绪。");
        }
    }

    /// <summary>注册全局热键(MOD_*|VK_*),同步返回是否成功(冲突/被占用返回 false)。</summary>
    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tcs = Post(delegate
        {
            bool ok = NativeMethods.RegisterHotKey(_hwnd, id, modifiers, virtualKey);
            if (ok)
            {
                _registered[id] = (modifiers, virtualKey);
            }
            return ok;
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>注销热键(幂等)。</summary>
    public void Unregister(int id)
    {
        if (_disposed)
        {
            return;
        }
        var tcs = Post(delegate
        {
            if (_registered.Remove(id))
            {
                NativeMethods.UnregisterHotKey(_hwnd, id);
            }
            return true;
        });
        tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        // 在热键线程上注销全部并退出消息循环
        Func<bool> cleanup = () =>
        {
            foreach (var id in _registered.Keys)
            {
                NativeMethods.UnregisterHotKey(_hwnd, id);
            }
            _registered.Clear();
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            return true;
        };
        _queue.Enqueue((TaskCompleted, cleanup));
        NativeMethods.PostMessageW(_hwnd, WmAppDispatch, IntPtr.Zero, IntPtr.Zero);
        if (!_thread.Join(5000))
        {
            _thread.Interrupt();
        }
        _ready.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly TaskCompletionSource<bool> TaskCompleted = new(true); // Dispose 路径复用

    private TaskCompletionSource<bool> Post(Func<bool> work)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((tcs, work));
        NativeMethods.PostMessageW(_hwnd, WmAppDispatch, IntPtr.Zero, IntPtr.Zero);
        return tcs;
    }

    private void ThreadProc()
    {
        _threadId = (uint)Environment.CurrentManagedThreadId;

        var wndProc = new NativeMethods.WndProc(WndProc);
        _sharedWndProc = wndProc; // 防回收

        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = _className,
        };
        if (NativeMethods.RegisterClassExW(ref wc) == 0)
        {
            throw new InvalidOperationException("RegisterClassExW 失败:" + NativeMethods.LastError(nameof(NativeMethods.RegisterClassExW)));
        }

        _hwnd = NativeMethods.CreateWindowExW(
            0, _className, "OpenSnip.Hotkey", 0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateWindowExW(HWND_MESSAGE) 失败:" + NativeMethods.LastError("CreateWindowExW"));
        }

        _ready.Set();

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == NativeMethods.WM_HOTKEY)
            {
                OnHotkey((int)msg.wParam.ToInt64());
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
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                item.Tcs.TrySetResult(item.Work());
            }
            catch (Exception ex)
            {
                item.Tcs.TrySetException(ex);
            }
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            OnHotkey((int)wParam.ToInt64());
            return IntPtr.Zero;
        }
        if (msg == WmAppDispatch)
        {
            DrainQueue();
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnHotkey(int id)
    {
        try
        {
            HotkeyPressed?.Invoke(id);
        }
        catch
        {
            // 事件订阅方异常不影响消息循环
        }
    }
}
