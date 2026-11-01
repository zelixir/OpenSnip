using OpenSnip.Core;

namespace OpenSnip.Pin;

/// <summary>
/// 贴图管理器(契约 §2.9):单 STA 消息循环线程托管全部贴图窗口。
/// <see cref="Pin"/> 可在任意线程调用,内部经队列投递到贴图线程同步创建(返回时窗口已建成并完成首帧提交)。
/// M2a(§11):新增虚拟桌面归属只读查询;并持有伪透明背景所需的截屏服务。
/// </summary>
public static class PinManager
{
    private static readonly object Gate = new();
    private static PinDispatcher? _dispatcher;
    private static readonly List<PinWindow> Pins = new();

    private static readonly object CaptureGate = new();
    private static ScreenCaptureService? _capture;      // 伪透明背景截屏服务(懒加载)
    private static bool _captureCreateFailed;           // 构造失败后不再反复重试

    /// <summary>
    /// 创建贴图窗口并显示(同步)。
    /// </summary>
    /// <param name="image">贴图内容。所有权移交 PinWindow:窗口 Close 后自动 Dispose(契约 §3.3),调用方不得复用。</param>
    /// <param name="initialRect">初始显示位置与尺寸(虚拟桌面物理 px);宽 ≤ 0 时按原图 1:1 尺寸显示。</param>
    /// <returns>贴图窗口实例。</returns>
    /// <exception cref="ArgumentException">image 为 null、已释放或尺寸非法。</exception>
    public static PinWindow Pin(NativeBitmap image, PxRect initialRect)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
        {
            image.Dispose();   // 所有权语义:无效入参即释放,避免调用方泄漏非托管内存
            throw new ArgumentException("image 必须是尺寸合法的位图", nameof(image));
        }
        try
        {
            _ = image.Scan0;   // 已释放时 Core 抛 ObjectDisposedException,转成更明确的入参错误
        }
        catch (ObjectDisposedException)
        {
            throw new ArgumentException("image 已被释放", nameof(image));
        }

        var dispatcher = EnsureDispatcher();
        bool entered = false;   // 标记闭包是否真的执行过(线程意外退出时不误判所有权)
        try
        {
            return dispatcher.Invoke(() =>
            {
                entered = true;
                var win = new PinWindow(dispatcher, image, initialRect);
                lock (Gate)
                {
                    Pins.Add(win);
                }
                return win;
            });
        }
        catch
        {
            if (!entered)
            {
                image.Dispose();   // 窗口从未开始创建:所有权退回,兜底释放
            }
            // 已开始创建的失败路径由 PinWindow 构造器自清理(含位图所有权),此处不再动
            throw;
        }
    }

    /// <summary>当前存活的贴图窗口快照。</summary>
    public static IReadOnlyList<PinWindow> ActivePins
    {
        get
        {
            lock (Gate)
            {
                return Pins.ToArray();
            }
        }
    }

    /// <summary>
    /// 贴图窗口是否位于当前虚拟桌面(IVirtualDesktopManager COM,v1 只读;契约 §11)。线程安全。
    /// 失败语义(有意保守):窗口已销毁/句柄无效 → false;COM 不可用或查询失败 → true
    /// (当作"在当前桌面",避免调用方据此隐藏贴图时误伤)。本方法不抛异常。
    /// </summary>
    public static bool IsOnCurrentVirtualDesktop(PinWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return VirtualDesktopHelper.IsOnCurrentVirtualDesktop(window.GetLiveHwnd());
    }

    /// <summary>关闭全部贴图窗口并停止贴图线程;之后可再次 Pin(按需重启线程与截屏服务)。</summary>
    public static void Shutdown()
    {
        PinDispatcher? dispatcher;
        lock (Gate)
        {
            dispatcher = _dispatcher;
        }
        if (dispatcher is null)
        {
            return;
        }

        try
        {
            dispatcher.Invoke(() =>
            {
                PinWindow[] snapshot;
                lock (Gate)
                {
                    snapshot = Pins.ToArray();
                }
                foreach (var w in snapshot)
                {
                    w.Close();   // 在贴图线程内调用:内联执行 DestroyWindow
                }
                NativeMethods.PostQuitMessage(0);
            });
        }
        catch (InvalidOperationException)
        {
            // 贴图线程已退出:其窗口已随线程销毁,无需处理
        }
        finally
        {
            if (!dispatcher.IsPinThread)
            {
                _ = dispatcher.Thread.Join(TimeSpan.FromSeconds(3));
            }
            lock (Gate)
            {
                if (ReferenceEquals(_dispatcher, dispatcher))
                {
                    _dispatcher = null;
                }
            }
            // 截屏服务(含 D3D 设备/DD 会话)随 Shutdown 一并释放,下次 Pin 重新懒加载
            lock (CaptureGate)
            {
                _capture?.Dispose();
                _capture = null;
                _captureCreateFailed = false;
            }
        }
    }

    /// <summary>取(或惰性创建)贴图线程。</summary>
    internal static PinDispatcher EnsureDispatcher()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }
            var d = new PinDispatcher();
            _dispatcher = d;
            return d;
        }
    }

    /// <summary>
    /// 伪透明背景截屏服务(懒加载;构造失败返回 null 并 PerfLog,不再重试直至 Shutdown 复位)。
    /// 仅在贴图线程使用(ScreenCaptureService 非线程安全)。
    /// </summary>
    internal static ScreenCaptureService? EnsureCaptureService()
    {
        lock (CaptureGate)
        {
            if (_captureCreateFailed)
            {
                return null;
            }
            if (_capture is not null)
            {
                return _capture;
            }
            try
            {
                _capture = new ScreenCaptureService();
                return _capture;
            }
            catch (Exception ex)
            {
                _captureCreateFailed = true;
                PerfLog.Log("Pin", "capture-service create failed: " + ex.Message);
                return null;
            }
        }
    }

    /// <summary>窗口销毁后由 PinWindow 回调移除(贴图线程)。</summary>
    internal static void RemoveWindow(PinWindow window)
    {
        lock (Gate)
        {
            _ = Pins.Remove(window);
        }
    }
}
