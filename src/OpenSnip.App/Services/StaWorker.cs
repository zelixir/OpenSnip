using System.Collections.Concurrent;
using OpenSnip.App.Interop;
using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// 专用 STA 工作线程:剪贴板读写、文件对话框等需要 OLE·STA 的操作统一投递到此线程。
/// 线程启动时 OleInitialize,满足 ClipboardWriter"调用线程需已 OLE 初始化(App 侧保证 STA)"的契约(§2.6)。
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    private StaWorker()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "OpenSnip.StaWorker",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public static StaWorker StartNew()
    {
        var w = new StaWorker();
        w._thread.Start();
        return w;
    }

    private void Loop()
    {
        var hr = NativeMethods.OleInitialize(IntPtr.Zero);
        if (hr != 0 && hr != unchecked((int)0x8000_0001)) // S_OK/S_FALSE 之外(如 RPC_E_CHANGED_MODE)记一笔
        {
            PerfLog.Log("App", $"sta-worker:ole-initialize hr=0x{hr:X8}");
        }
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    PerfLog.Log("App", $"sta-worker:action-failed {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (hr == 0)
            {
                NativeMethods.OleUninitialize(); // 仅 S_OK 时配对释放;S_FALSE 无需(计数属先前的初始化者)
            }
        }
    }

    /// <summary>同步执行;若当前已在 STA 线程则内联执行,避免自等待死锁。</summary>
    public T Invoke<T>(Func<T> func)
    {
        if (Thread.CurrentThread == _thread)
        {
            return func();
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>同步执行无返回值操作。</summary>
    public void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    /// <summary>投递异步执行,不等待结果。</summary>
    public void Post(Action action) => _queue.Add(action);

    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
    }
}
