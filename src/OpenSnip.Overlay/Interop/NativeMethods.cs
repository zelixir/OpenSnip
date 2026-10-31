// OpenSnip.Overlay —— Win32 P/Invoke 最小集(仅供本类库内部使用)
// 红线 §0.5:此处不承担截屏(截屏在 Core),只有窗口/GDI 背衬辅助。
using System.Runtime.InteropServices;

namespace OpenSnip.Overlay.Interop;

/// <summary>窗口消息常量(仅 Overlay 用到的子集)。</summary>
internal static class WM
{
    public const uint DESTROY = 0x0002;
    public const uint PAINT = 0x000F;
    public const uint CLOSE = 0x0010;
    public const uint ERASEBKGND = 0x0014;
    public const uint SETCURSOR = 0x0020;
    public const uint KEYDOWN = 0x0100;
    public const uint SYSKEYDOWN = 0x0104;
    public const uint MOUSEMOVE = 0x0200;
    public const uint LBUTTONDOWN = 0x0201;
    public const uint LBUTTONUP = 0x0202;
    public const uint LBUTTONDBLCLK = 0x0203;
    public const uint RBUTTONDOWN = 0x0204;
    public const uint MOUSEWHEEL = 0x020A;

    // —— §8.1 测试驱动 Post*(内部线程封送,与 WM.APP 基址段区分)——
    public const uint APP_SETREGION = 0x8001;
    public const uint APP_CONFIRM = 0x8002;
    public const uint APP_CANCEL = 0x8003;
    public const uint APP_SNAPSHOT = 0x8004; // §8.5:渲染 DIB 进程内导出(wParam=cookie)
}

/// <summary>杂项 Win32 常量。</summary>
internal static class Win32
{
    public const uint WS_POPUP = 0x80000000u;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const int CS_DBLCLKS = 0x0008;          // 双击消息必需
    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;                // 显示不激活,激活由主屏窗口统一接管
    public const uint ULW_ALPHA = 0x00000002;
    public const byte AC_SRC_OVER = 0;
    public const byte AC_SRC_ALPHA = 1;            // 像素含 alpha(预乘 BGRA)
    public const int DIB_RGB_COLORS = 0;
    public const int BI_RGB = 0;
    public const uint PM_REMOVE = 0x0001;

    // 标准光标 OCR_*
    public const int OCR_ARROW = 32512;
    public const int OCR_CROSS = 32515;
    public const int OCR_SIZENWSE = 32642;
    public const int OCR_SIZENESW = 32643;
    public const int OCR_SIZEWE = 32644;
    public const int OCR_SIZENS = 32645;
    public const int OCR_SIZEALL = 32646;

    public const int HTCLIENT = 1;

    // 虚拟键
    public const int VK_SHIFT = 0x10;
    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;

    /// <summary>Per-Monitor V2 线程 DPI 上下文:保证全部坐标 API 都是物理像素。</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
    public POINT(int x, int y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int CX;
    public int CY;
    public SIZE(int cx, int cy) { CX = cx; CY = cy; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;

    /// <summary>逐像素 alpha 的 SrcOver(ULW 标准组合)。</summary>
    public static BLENDFUNCTION SrcOverPerPixel() => new()
    {
        BlendOp = Win32.AC_SRC_OVER,
        BlendFlags = 0,
        SourceConstantAlpha = 255,
        AlphaFormat = Win32.AC_SRC_ALPHA,
    };
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;       // 负值 = 顶行在前(top-down),与 Skia 行序一致
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;

    public static BITMAPINFOHEADER TopDown32(int width, int height) => new()
    {
        biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth = width,
        biHeight = -height,
        biPlanes = 1,
        biBitCount = 32,
        biCompression = Win32.BI_RGB,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    public uint bmiColors;     // BI_RGB 32bpp 不需要调色板,占位即可
}

/// <summary>WndProc 原生回调委托(引用必须静态持有,防 GC 回收导致崩溃)。</summary>
internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

/// <summary>P/Invoke 入口。</summary>
internal static class NativeMethods
{
    // —— 窗口类/窗口 ——
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    // —— 鼠标 ——
    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);

    // —— ULW ——
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    // —— 消息循环 ——
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

    // —— DC/GDI 背衬 ——
    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    // —— DPI/模块 ——
    [DllImport("user32.dll")]
    internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
