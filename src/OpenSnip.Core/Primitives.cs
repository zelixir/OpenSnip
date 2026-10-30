namespace OpenSnip.Core;

/// <summary>
/// 物理像素矩形(全项目坐标一律物理 px,Per-Monitor DPI V2 清单由 App 声明)。
/// </summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    /// <summary>空矩形(0,0,0,0)。</summary>
    public static PxRect Empty => default;

    /// <summary>判断物理坐标点是否落在矩形内(左闭右开区间)。</summary>
    public bool Contains(int x, int y)
        => x >= X && x < X + Width && y >= Y && y < Y + Height;

    /// <summary>宽或高 &lt;= 0 视为空矩形。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>物理像素点。</summary>
public readonly record struct PxPoint(int X, int Y);

/// <summary>显示器描述(所有坐标均为物理像素虚拟桌面坐标)。</summary>
public sealed record DisplayInfo(
    int Index,            // 0-based,EnumDisplayMonitors 顺序
    string DeviceName,    // \\\.\DISPLAY1
    PxRect Bounds,        // 物理像素虚拟桌面坐标
    PxRect WorkArea,
    bool IsPrimary,
    float DpiScale,       // 1.0/1.25/1.5...
    bool IsHdr);          // DXGI_COLOR_SPACE 检测,不可得时 false

/// <summary>截屏模式:DXGI Desktop Duplication 主路径 / GDI BitBlt 回退。</summary>
public enum CaptureMode
{
    DxgiDuplication,
    GdiBitBlt,
}
