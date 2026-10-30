using System.Runtime.InteropServices;
using System.Text;

namespace OpenSnip.Core;

/// <summary>顶层窗口描述(物理像素 Bounds)。</summary>
public sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ClassName,
    string ExeName,
    PxRect Bounds,
    bool IsTopmost,
    bool IsToolWindow);

/// <summary>
/// 可命中顶层窗口枚举(Z 序,顶在前)。
/// 过滤:不可见 / DWM cloaked / WS_EX_TRANSPARENT / 零尺寸 / DirectComposition 桥接窗口。
/// </summary>
public static class WindowEnumerator
{
    // EnumWindows 回调存静态字段防 GC(契约 §3.1 纪律)
    private static readonly NativeMethods.EnumWindowsProc Callback = OnWindow;

    private static readonly object Gate = new();
    private static List<nint>? _collecting;

    // DirectComposition / XAML 桥接窗口没有独立可命中语义(契约"OwnsComposition"过滤的实现解释)
    private static readonly HashSet<string> CompositionBridgeClasses = new(StringComparer.Ordinal)
    {
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "Microsoft.UI.Composition.DesktopWindowContentBridge",
        "Windows.UI.Composition.DesktopWindowContentBridgeInternal",
    };

    public static IReadOnlyList<WindowInfo> GetHitTestableTopLevel()
    {
        var raw = new List<nint>();
        lock (Gate)
        {
            _collecting = raw;
            try
            {
                // 临时切 Per-Monitor V2 线程上下文:GetWindowRect 返回物理像素,
                // 与 DisplayManager/截屏坐标系一致(App 未带清单的宿主进程同样正确)
                using (NativeMethods.SetPerMonitorV2Context())
                {
                    if (!NativeMethods.EnumWindows(Callback, IntPtr.Zero))
                    {
                        throw new InvalidOperationException("EnumWindows 失败:" + NativeMethods.LastError(nameof(NativeMethods.EnumWindows)));
                    }
                }
            }
            finally
            {
                _collecting = null;
            }
        }

        // EnumWindows 返回顺序即 Z 序(顶在前),保持原序
        var result = new List<WindowInfo>(raw.Count);
        foreach (var hwnd in raw)
        {
            if (Build(hwnd) is { } info)
            {
                result.Add(info);
            }
        }
        return result;
    }

    private static bool OnWindow(IntPtr hwnd, IntPtr lParam)
    {
        _collecting?.Add(hwnd);
        return true;
    }

    private static WindowInfo? Build(IntPtr hwnd)
    {
        // 1. 可见性
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return null;
        }

        // 2. DWM cloaked(UWP 挂起壳/虚拟桌面隐藏窗口)
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0)
        {
            return null;
        }

        // 3. 扩展样式:WS_EX_TRANSPARENT 直接不可命中;TOPMOST/TOOLWINDOW 记录
        uint exStyle = (uint)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0)
        {
            return null;
        }

        // 4. 零尺寸
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // 5. 类名(顺带过滤 Composition 桥接)
        string className = GetClassName(hwnd);
        if (CompositionBridgeClasses.Contains(className))
        {
            return null;
        }

        string title = GetTitle(hwnd);
        string exeName = GetExeName(hwnd);

        return new WindowInfo(
            Handle: hwnd,
            Title: title,
            ClassName: className,
            ExeName: exeName,
            Bounds: new PxRect(rect.Left, rect.Top, width, height),
            IsTopmost: (exStyle & NativeMethods.WS_EX_TOPMOST) != 0,
            IsToolWindow: (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0);
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = NativeMethods.GetWindowTextW(hwnd, [], 0);
        if (len <= 0)
        {
            return string.Empty;
        }
        var buf = new char[Math.Min(len + 1, 512)];
        int copied = NativeMethods.GetWindowTextW(hwnd, buf, buf.Length);
        int n = Array.IndexOf(buf, '\0');
        if (n < 0)
        {
            n = Math.Min(copied, buf.Length);
        }
        return new string(buf, 0, n);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        int len = NativeMethods.GetClassNameW(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : string.Empty;
    }

    private static string GetExeName(IntPtr hwnd)
    {
        if (NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0)
        {
            return string.Empty;
        }
        nint hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == 0)
        {
            return string.Empty; // 受限进程(SYSTEM/提权)拿不到路径
        }
        try
        {
            var buf = new char[1024];
            uint size = (uint)buf.Length;
            if (NativeMethods.QueryFullProcessImageNameW(hProcess, 0, buf, ref size))
            {
                return Path.GetFileName(new string(buf, 0, (int)size));
            }
            return string.Empty;
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }
}

/// <summary>
/// 窗口命中测试:返回覆盖指定物理屏幕点的顶层窗口 Z 序列表(第一个=最顶)。
/// Overlay 悬停高亮用 [0],滚轮切层级用整个列表(契约 §2.4)。
/// </summary>
public sealed class WindowHitTester
{
    /// <summary>
    /// GetHitTestableTopLevel() 本身按 Z 序枚举(与 WindowFromPoint 语义一致),
    /// 再以 PxRect.Contains 判定覆盖,天然支持负坐标虚拟桌面(契约 §3.1)。
    /// </summary>
    public IReadOnlyList<WindowInfo> HitTopToBottom(PxPoint screenPt)
    {
        var all = WindowEnumerator.GetHitTestableTopLevel();
        var result = new List<WindowInfo>();
        foreach (var w in all)
        {
            if (w.Bounds.Contains(screenPt.X, screenPt.Y))
            {
                result.Add(w);
            }
        }
        return result;
    }
}
