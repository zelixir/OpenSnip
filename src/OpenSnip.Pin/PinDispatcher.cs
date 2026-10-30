using System.Collections.Concurrent;

namespace OpenSnip.Pin;

/// <summary>
/// 贴图专用 STA 消息循环线程(契约 §3.3:PinManager 单线程托管全部 PinWindow 的 HWND)。
/// 通过隐藏的消息窗口接收 WM_APP_DISPATCH 唤醒并排空并发工作队列;
/// 一切针对 PinWindow HWND 的 Win32 调用都在该线程执行。
/// </summary>
internal sealed class PinDispatcher
{
    /// <summary>隐藏宿主窗口类名(进程级注册一次)。</summary>
    internal const string HostClassName = "OpenSnip.Pin.Host";

    /// <summary>贴图线程名(自测用 Closed 事件线程校验即依赖此名)。</summary>
    internal const string ThreadName = "OpenSnip.Pin";

    private sealed record WorkItem(Action Run, TaskCompletionSource<object?> Done);

    /// <summary>静态持有窗口过程委托防 GC(契约 §3.1 同款纪律)。</summary>
    private static readonly NativeMethods.WndProcDelegate HostWndProcStatic = HostWndProc;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile IntPtr _hostHwnd;
    private volatile bool _loopExited;

    public PinDispatcher()
    {
        _thread = new Thread(RunMessageLoop)
        {
            Name = ThreadName,
            IsBackground = true,   // 进程退出不被贴图线程吊住;正常关闭走 Shutdown 的显式 Join
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(8)))
        {
            throw new InvalidOperationException("贴图线程启动超时(消息宿主窗口创建失败?)");
        }
    }

    /// <summary>贴图线程对象(Shutdown 收尾 Join 用)。</summary>
    internal Thread Thread => _thread;

    /// <summary>当前线程是否为贴图线程。</summary>
    public bool IsPinThread => Thread.CurrentThread == _thread;

    /// <summary>
    /// 在贴图线程同步执行 func(调用线程阻塞至完成;若已在贴图线程则直接内联执行,避免自死锁)。
    /// 贴图线程已退出时抛 <see cref="InvalidOperationException"/>,绝不悬挂调用方。
    /// </summary>
    public T Invoke<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (IsPinThread)
        {
            return func();
        }

        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(new WorkItem(() =>
        {
            try { done.TrySetResult(func()); }
            catch (Exception ex) { done.TrySetException(ex); }
        }, done));

        if (_loopExited)
        {
            throw new InvalidOperationException("贴图线程已退出,无法执行贴图操作");
        }
        PostDispatchWake();
        return (T)done.Task.GetAwaiter().GetResult()!;
    }

    /// <summary><see cref="Invoke{T}(Func{T})"/> 的无返回值版本。</summary>
    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    /// <summary>唤醒消息循环(入队后必须调用,否则队列不会被消费)。</summary>
    private void PostDispatchWake()
    {
        var host = _hostHwnd;
        if (host != IntPtr.Zero)
        {
            _ = NativeMethods.PostMessageW(host, NativeMethods.WM_APP_DISPATCH, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>排空队列。WorkItem.Run 自身保证不抛(异常已折入 TCS)。</summary>
    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var item))
        {
            item.Run();
        }
    }

    private void RunMessageLoop()
    {
        // 注册宿主窗口类(进程级幂等:上一代 dispatcher 注册过时忽略 1410)
        var wc = new NativeMethods.WNDCLASSEX
        {
            CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            Style = 0,
            LpfnWndProc = HostWndProcStatic,
            HInstance = NativeMethods.GetModuleHandleW(null),
            HCursor = NativeMethods.LoadCursorW(IntPtr.Zero, NativeMethods.IDC_ARROW),
            LpszClassName = HostClassName,
        };
        _ = NativeMethods.RegisterClassExW(ref wc);

        _hostHwnd = NativeMethods.CreateWindowExW(
            0, HostClassName, HostClassName, 0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, wc.HInstance, IntPtr.Zero);
        _ready.Set();
        if (_hostHwnd == IntPtr.Zero)
        {
            _loopExited = true;
            return;
        }

        // GetMessage 返回 0 = WM_QUIT,-1 = 错误;两种情况都退出循环
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == NativeMethods.WM_APP_DISPATCH && msg.Hwnd == _hostHwnd)
            {
                DrainQueue();
            }
            else
            {
                _ = NativeMethods.TranslateMessage(ref msg);
                _ = NativeMethods.DispatchMessageW(ref msg);
            }
        }

        _loopExited = true;
        // 循环退出后排干队列并失败结算,保证 Invoke 调用方不悬挂
        while (_queue.TryDequeue(out var item))
        {
            item.Done.TrySetException(new InvalidOperationException("贴图线程已退出,无法执行贴图操作"));
        }
        _ = NativeMethods.DestroyWindow(_hostHwnd);
        _hostHwnd = IntPtr.Zero;
    }

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
}
