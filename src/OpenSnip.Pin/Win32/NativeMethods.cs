using System.Runtime.InteropServices;

namespace OpenSnip.Pin;

/// <summary>
/// Win32 互操作声明(仅本类库内部使用;无 WPF/WinForms 依赖,红线 §0.7)。
/// 坐标一律物理像素:进程 DPI 感知由 App 侧 Per-Monitor V2 清单声明(契约 §2.1);
/// Core 的 DisplayManager 在枚举时已自带 Per-Monitor V2 线程上下文,拿到的即为物理值。
/// </summary>
internal static unsafe class NativeMethods
{
    // ===== 窗口样式(契约 §3.3:WS_POPUP|WS_EX_LAYERED|WS_EX_TOPMOST|WS_EX_TOOLWINDOW)=====
    internal const uint WS_POPUP = 0x80000000u;
    internal const uint WS_VISIBLE = 0x10000000u;

    internal const uint WS_EX_TOPMOST = 0x00000008u;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080u;
    internal const uint WS_EX_TRANSPARENT = 0x00000020u;
    internal const uint WS_EX_LAYERED = 0x00080000u;
    internal const uint WS_EX_NOACTIVATE = 0x08000000u;

    /// <summary>窗口类样式:CS_DBLCLKS 使窗口接收 WM_LBUTTONDBLCLK(双击关闭依赖)。</summary>
    internal const uint CS_DBLCLKS = 0x0008u;

    // ===== 消息 =====
    internal const uint WM_NULL = 0x0000u;
    internal const uint WM_NCCREATE = 0x0001u;
    internal const uint WM_DESTROY = 0x0002u;
    internal const uint WM_CLOSE = 0x0010u;
    internal const uint WM_ERASEBKGND = 0x0014u;
    internal const uint WM_NCDESTROY = 0x0082u;
    internal const uint WM_MOUSEMOVE = 0x0200u;
    internal const uint WM_LBUTTONDOWN = 0x0201u;
    internal const uint WM_LBUTTONUP = 0x0202u;
    internal const uint WM_LBUTTONDBLCLK = 0x0203u;
    internal const uint WM_RBUTTONUP = 0x0205u;
    internal const uint WM_MOUSEWHEEL = 0x020Au;
    internal const uint WM_APP = 0x8000u;
    internal const uint WM_TIMER = 0x0113u;
    internal const uint WM_KEYDOWN = 0x0100u;

    /// <summary>调度唤醒消息:贴图线程收到后排空工作队列。</summary>
    internal const uint WM_APP_DISPATCH = WM_APP + 1;

    // ===== 鼠标修饰键 =====
    internal const int MK_LBUTTON = 0x0001;
    internal const int MK_CONTROL = 0x0008;

    // ===== SetWindowPos / Z 序 =====
    internal const uint SWP_NOSIZE = 0x0001u;
    internal const uint SWP_NOMOVE = 0x0002u;
    internal const uint SWP_NOZORDER = 0x0004u;
    internal const uint SWP_NOACTIVATE = 0x0010u;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    /// <summary>消息专用窗口父句柄(HWND_MESSAGE)。</summary>
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    // ===== UpdateLayeredWindow(契约 §3.3 内容提交通道)=====
    internal const uint ULW_ALPHA = 0x0002u;
    internal const byte AC_SRC_OVER = 0x00;
    internal const byte AC_SRC_ALPHA = 0x01;

    // ===== TrackPopupMenu(契约 §3.3 右键菜单)=====
    internal const uint TPM_RIGHTBUTTON = 0x0002u;
    internal const uint TPM_RETURNCMD = 0x0100u;
    internal const uint MF_STRING = 0x0000u;
    internal const uint MF_CHECKED = 0x0008u;
    internal const uint MF_SEPARATOR = 0x0800u;
    internal const uint MF_POPUP = 0x0010u;

    /// <summary>右键菜单命令 ID(仅内部;M2 可扩展"保存为 PNG")。</summary>
    internal const uint MENU_ID_TOPMOST = 1;
    internal const uint MENU_ID_CLICKTHROUGH = 2;
    internal const uint MENU_ID_CLOSE = 3;
    internal const uint MENU_ID_CROP = 4;

    /// <summary>背景模式子菜单首 ID:FIRST+n,n=0..3 依次对应 PinBackground 四模式(契约 §11)。</summary>
    internal const uint MENU_ID_BG_FIRST = 0x10u;

    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int BI_RGB = 0;
    internal const int DIB_RGB_COLORS = 0;
    internal const uint IDC_ARROW = 32512u;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    // ===== 结构体 =====

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Pt;
    }

    /// <summary>ULW 混合参数:AC_SRC_OVER + AC_SRC_ALPHA(逐像素预乘 alpha)。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    /// <summary>窗口过程委托;必须由调用方静态字段持有防 GC(契约 §3.1 同款纪律)。</summary>
    internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint CbSize;
        public uint Style;
        public WndProcDelegate? LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpszClassName;
        public IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;   // 负值 = top-down,与 Skia 行序一致
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER BmiHeader;
        public uint BmiColors;   // 32bpp 不需要调色板,仅占位保持布局
    }

    // ===== API:user32 =====

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    /// <summary>分层窗口内容提交:预乘 BGRA DIB → 屏幕(契约 §3.3 ULW 通道)。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, POINT* pptDst, SIZE* psize,
        IntPtr hdcSrc, POINT* pptSrc, int crKey, BLENDFUNCTION* pblend, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    internal static extern uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheckFlags);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>TPM_RETURNCMD 时返回值即所选命令 ID(0=未选)。</summary>
    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr LoadCursorW(IntPtr hInstance, uint lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool KillTimer(IntPtr hWnd, IntPtr nIDEvent);

    // ===== API:gdi32 =====

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr hObject);

    // ===== API:kernel32 =====

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
