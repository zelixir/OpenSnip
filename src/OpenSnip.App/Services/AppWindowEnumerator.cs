using System.Globalization;
using System.Text;
using OpenSnip.App.Interop;
using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// 本进程顶层窗口枚举(§8.2 status 用):
/// 遮罩窗口按注册类名识别(SPEC §3.2 固定 "OpenSnip.Overlay"),
/// 贴图窗口契约未暴露 hwnd,按"本进程可见顶层窗口矩形 == CurrentRect"反查。
/// </summary>
internal static class AppWindowEnumerator
{
    /// <summary>遮罩窗口注册类名(与 OpenSnip.Overlay 的 OverlayWindow.ClassName 对应)。</summary>
    internal const string OverlayClassName = "OpenSnip.Overlay";

    internal readonly record struct OwnWindow(IntPtr Hwnd, string ClassName, PxRect Rect);

    /// <summary>本进程全部可见顶层窗口(hwnd/类名/矩形)。</summary>
    public static List<OwnWindow> EnumerateOwnVisibleTopLevel()
    {
        var result = new List<OwnWindow>();
        uint ownPid = (uint)Environment.ProcessId;
        var proc = new NativeMethods.EnumWindowsProc((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }
            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var windowPid) != 0 && windowPid != ownPid)
            {
                return true;
            }
            var sb = new StringBuilder(256);
            _ = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            if (!NativeMethods.GetWindowRect(hwnd, out var rc))
            {
                return true;
            }
            result.Add(new OwnWindow(
                hwnd, sb.ToString(),
                new PxRect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top)));
            return true;
        });
        _ = NativeMethods.EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return result;
    }

    /// <summary>遮罩窗口 hwnd 列表(十进制字符串,§8.2 与 vdesk JSON 约定一致)。</summary>
    public static IReadOnlyList<string> GetOverlayWindowHwnds()
        => EnumerateOwnVisibleTopLevel()
            .Where(w => w.ClassName == OverlayClassName)
            .Select(w => w.Hwnd.ToInt64().ToString(CultureInfo.InvariantCulture))
            .ToList();
}
