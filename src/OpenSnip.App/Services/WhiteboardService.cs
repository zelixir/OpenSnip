using System.Diagnostics;
using OpenSnip.Core;
using OpenSnip.Overlay;

namespace OpenSnip.App.Services;

/// <summary>
/// §15 白板会话接入:包装 WhiteboardSession 的生命周期(Start → Confirmed(png)/Cancelled → Dispose)。
/// 屏幕集合:v1 默认全部显示器;--active-screen 时取前台窗口所在屏(回退主屏)。
/// Run 阻塞至会话退出(CLI 语义);RPC 侧自行 Task.Run + 受理即应答。
/// </summary>
public sealed class WhiteboardService
{
    /// <summary>启动白板并阻塞至退出;返回 (ok, error, 确认导出的 png 可空)。</summary>
    public (bool Ok, string? Error, byte[]? Png) Run(int argb, bool activeScreenOnly)
    {
        var total = Stopwatch.StartNew();
        try
        {
            var displays = DisplayManager.Instance.Displays;
            if (displays.Count == 0)
            {
                return (false, "未枚举到任何显示器", null);
            }
            IReadOnlyList<DisplayInfo> targets = displays;
            if (activeScreenOnly)
            {
                var act = ActiveScreen(displays);
                if (act is not null)
                {
                    targets = new[] { act };
                }
            }

            var session = new WhiteboardSession(targets, argb);
            var done = new TaskCompletionSource<byte[]?>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            session.Confirmed += png => done.TrySetResult(png);
            session.Cancelled += () => done.TrySetResult(null);

            session.Start();
            PerfLog.Log("App", $"whiteboard:started screens={targets.Count} argb=0x{argb:X8} t={AppClock.ElapsedMs}ms");
            var png = done.Task.GetAwaiter().GetResult(); // 阻塞至确认/取消(与 SnipOverlay 等待模式一致)
            session.Dispose();
            PerfLog.Log("App", $"whiteboard:done confirmed={png is not null} bytes={png?.Length ?? 0} total={total.Elapsed.TotalMilliseconds:F1}ms");
            return (true, null, png);
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"whiteboard:error {ex}");
            return (false, ex.Message, null);
        }
    }

    /// <summary>前台窗口所在屏(取窗口矩形中心);失败回退主屏。</summary>
    private static DisplayInfo? ActiveScreen(IReadOnlyList<DisplayInfo> displays)
    {
        var hwnd = Interop.NativeMethods.GetForegroundWindow();
        if (hwnd != IntPtr.Zero && Interop.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            var center = new PxPoint((rc.Left + rc.Right) / 2, (rc.Top + rc.Bottom) / 2);
            var hit = DisplayManager.Instance.GetDisplayAt(center);
            if (hit is not null)
            {
                return hit;
            }
        }
        return displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
    }
}
