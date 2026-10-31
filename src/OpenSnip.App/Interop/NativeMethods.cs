using System.Runtime.InteropServices;
using System.Text;

namespace OpenSnip.App.Interop;

/// <summary>
/// App 层专用 Win32 P/Invoke:父进程控制台附加(CLI 回显)、剪贴板读取(CF_DIB / "PNG" / CF_HDROP)、
/// STA 工作线程 OLE 初始化。与 Core 内部互操作互不冲突(各程序集独立声明)。
/// </summary>
internal static class NativeMethods
{
    public const int ATTACH_PARENT_PROCESS = -1;
    public const uint CF_DIB = 8;
    public const uint CF_HDROP = 15;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("ole32.dll")]
    public static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    public static extern void OleUninitialize();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    public static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    // —— 本进程顶层窗口枚举(§8.2 status:遮罩/贴图 hwnd 反查)——

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>附加父进程控制台(WinExe 从 cmd 启动时回显 CLI 输出);失败静默(如资源管理器启动)。</summary>
    public static void TryAttachParentConsole()
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                return;
            }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 控制台已分离等,静默 */ }
        }
        catch { /* 静默 */ }
    }

    /// <summary>退出前解除控制台附加(避免父 cmd 不回提示符)。</summary>
    public static void TryFreeConsole()
    {
        try { FreeConsole(); } catch { /* 静默 */ }
    }
}
