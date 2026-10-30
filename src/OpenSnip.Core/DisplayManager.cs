using System.Runtime.InteropServices;

namespace OpenSnip.Core;

/// <summary>
/// 显示器管理:构造时枚举全部显示器(物理像素),懒加载单例。
/// </summary>
public sealed class DisplayManager
{
    private static readonly Lazy<DisplayManager> LazyInstance =
        new(() => new DisplayManager(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>懒加载单例。</summary>
    public static DisplayManager Instance => LazyInstance.Value;

    /// <summary>枚举到的显示器列表(EnumDisplayMonitors 顺序,0-based)。</summary>
    public IReadOnlyList<DisplayInfo> Displays { get; }

    public DisplayManager()
    {
        Displays = EnumerateDisplays();
    }

    /// <summary>返回包含该物理屏幕坐标点的显示器;不在任何显示器上返回 null。</summary>
    public DisplayInfo? GetDisplayAt(PxPoint screenPt)
    {
        foreach (var d in Displays)
        {
            if (d.Bounds.Contains(screenPt.X, screenPt.Y))
            {
                return d;
            }
        }
        return null;
    }

    /// <summary>按 0-based 索引取显示器;越界返回 null。</summary>
    public DisplayInfo? GetDisplayByIndex(int index)
        => index >= 0 && index < Displays.Count ? Displays[index] : null;

    private static List<DisplayInfo> EnumerateDisplays()
    {
        var monitors = new List<nint>();
        // 回调委托存静态字段防 GC(契约 §3.1 纪律)
        NativeMethods.EnumWindowsProc proc = (hMonitor, _) =>
        {
            monitors.Add(hMonitor);
            return true;
        };
        try
        {
            // 临时切 Per-Monitor V2 线程上下文:保证 MONITORINFO 为物理像素
            // (Core 自身不依赖 App 的清单;测试宿主等非感知进程下也正确)
            using (NativeMethods.SetPerMonitorV2Context())
            {
                if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero))
                {
                    throw new InvalidOperationException("EnumDisplayMonitors 失败:" + NativeMethods.LastError(nameof(NativeMethods.EnumDisplayMonitors)));
                }
            }
        }
        finally
        {
            GC.KeepAlive(proc);
        }

        var list = new List<DisplayInfo>(monitors.Count);
        for (int i = 0; i < monitors.Count; i++)
        {
            var mi = new NativeMethods.MONITORINFOEXW();
            mi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEXW>();
            if (!NativeMethods.GetMonitorInfoW(monitors[i], ref mi))
            {
                continue;
            }

            float dpiScale = GetDpiScale(monitors[i]);
            var bounds = new PxRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
            var work = new PxRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
            bool isHdr = HdrDetector.IsHdr(mi.szDevice);

            list.Add(new DisplayInfo(
                Index: list.Count, // 保持枚举顺序连续编号
                DeviceName: mi.szDevice,
                Bounds: bounds,
                WorkArea: work,
                IsPrimary: (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                DpiScale: dpiScale,
                IsHdr: isHdr));
        }
        return list;
    }

    private static float GetDpiScale(nint hMonitor)
    {
        using (NativeMethods.SetPerMonitorV2Context())
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96f;
            }
        }
        return 1f; // 不可得时按 100%
    }
}

/// <summary>
/// HDR 检测:DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 是否被输出支持。
/// 任何失败(无 DXGI 1.6、无 Vortice 类型、驱动异常)一律 false(契约:不可得时 false)。
/// </summary>
internal static class HdrDetector
{
    public static bool IsHdr(string deviceName)
    {
        try
        {
            return Probe(deviceName);
        }
        catch
        {
            return false;
        }
    }

    private static bool Probe(string deviceName)
    {
        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory2>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        if (!string.Equals(desc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        // DXGI_COLOR_SPACE 检测:IDXGIOutput6.Description1.ColorSpace
                        // == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020(原生值 2)即 HDR10
                        if (output.QueryInterface<Vortice.DXGI.IDXGIOutput6>() is { } out6)
                        {
                            using (out6)
                            {
                                var desc1 = out6.Description1;
                                return (int)desc1.ColorSpace == 2;
                            }
                        }
                        return false;
                    }
                }
            }
        }
        return false;
    }
}
