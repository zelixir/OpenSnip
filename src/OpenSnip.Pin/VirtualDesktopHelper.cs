using System.Runtime.InteropServices;
using OpenSnip.Core;

namespace OpenSnip.Pin;

/// <summary>
/// IVirtualDesktopManager COM 只读封装(契约 §11:虚拟桌面归属查询,v1 只读)。
/// 线程安全(对象为 agile COM,静态缓存 RCW)。
/// 失败语义(有意保守):COM 对象创建失败 / 查询返回失败 HRESULT / 抛异常 → 返回 true
/// (当作"在当前桌面",避免调用方据此隐藏贴图时误伤);句柄无效(含已销毁窗口)→ false。
/// 任何路径不抛异常、不长阻塞(单次轻量 COM 调用,非 UIA 树遍历,不做线程超时隔离)。
/// </summary>
internal static class VirtualDesktopHelper
{
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private static IVirtualDesktopManager? _manager;
    private static bool _createFailed;

    internal static bool IsOnCurrentVirtualDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            var mgr = GetManager();
            if (mgr is null)
            {
                return true;   // COM 不可用:保守当作在当前桌面
            }
            int hr = mgr.IsWindowOnCurrentVirtualDesktop(hwnd, out bool on);
            if (hr != 0)
            {
                PerfLog.Log("Pin", "virtual-desktop query hr=0x" + hr.ToString("X8") + " hwnd=" + hwnd);
                return true;
            }
            return on;
        }
        catch (Exception ex)
        {
            PerfLog.Log("Pin", "virtual-desktop query failed: " + ex.Message);
            return true;
        }
    }

    private static IVirtualDesktopManager? GetManager()
    {
        if (_manager is not null)
        {
            return _manager;
        }
        if (_createFailed)
        {
            return null;
        }
        try
        {
            // GetTypeFromCLSID + Activator.CreateInstance:标准 CoCreateInstance 路径,随后按接口 QI
            var type = Type.GetTypeFromCLSID(ClsidVirtualDesktopManager, throwOnError: false);
            if (type is null)
            {
                _createFailed = true;
                PerfLog.Log("Pin", "virtual-desktop COM type not found");
                return null;
            }
            _manager = (IVirtualDesktopManager)Activator.CreateInstance(type)!;
            return _manager;
        }
        catch (Exception ex)
        {
            _createFailed = true;
            PerfLog.Log("Pin", "virtual-desktop COM create failed: " + ex.Message);
            return null;
        }
    }
}

/// <summary>IVirtualDesktopManager(vtable:IUnknown + IsWindowOnCurrentVirtualDesktop;其余方法 v1 只读不声明)。</summary>
[ComImport]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    /// <summary>S_OK 时 onCurrent 有效;窗口必须属于调用进程。</summary>
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
}
