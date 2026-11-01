// §15 白板会话:全屏纯色面板 + M2a 标注全套。
// 组合(非复制)SnipOverlay:capture 回调返回纯色"冻结帧",WhiteboardMode 开启后
// 渲染层跳过遮罩/选区 UI/准线/放大镜,标注/工具条/撤销栈全部复用;
// 确认 = 主屏全幅 + 标注合成 → EncodePng → Confirmed(byte[]?);Esc/右键/工具条 ✗ = Cancelled。
using System.Diagnostics;
using OpenSnip.Core;
using SkiaSharp;

namespace OpenSnip.Overlay;

/// <summary>白板会话。事件在内部 STA 线程触发(与 SnipOverlay 一致)。</summary>
public sealed class WhiteboardSession : IDisposable
{
    private readonly IReadOnlyList<DisplayInfo> _displays;
    private readonly SnipOverlay _overlay;
    private readonly int _argb;

    /// <summary>确认导出:主屏(或唯一屏)全幅 PNG,标注已合成;null 仅在导出失败时出现。</summary>
    public event Action<byte[]?>? Confirmed;

    /// <summary>Esc/右键/工具条取消;直接退出(§15)。</summary>
    public event Action? Cancelled;

    /// <param name="displays">白板覆盖的屏幕(全部或 --active-screen 单屏,由 App 决定)。</param>
    /// <param name="argb">面板颜色,默认白 0xFFFFFFFF(支持半透明 alpha)。</param>
    public WhiteboardSession(IReadOnlyList<DisplayInfo> displays, int argb = unchecked((int)0xFFFFFFFF))
    {
        _displays = displays ?? throw new ArgumentNullException(nameof(displays));
        if (_displays.Count == 0) throw new ArgumentException("displays 不能为空", nameof(displays));
        _argb = argb;

        _overlay = new SnipOverlay(displays, MakeSolidFrame, null)
        {
            WhiteboardMode = true,
        };
        _overlay.Confirmed += OnOverlayConfirmed;
        _overlay.Cancelled += () =>
        {
            PerfLog.Log("Whiteboard.Cancelled", "esc/right-click/toolbar");
            Cancelled?.Invoke();
        };
    }

    /// <summary>启动(内部 STA 线程:纯色帧 → 全屏白板窗口 → 消息循环)。</summary>
    public void Start()
    {
        var sw = Stopwatch.StartNew();
        _overlay.Start();
        PerfLog.Log("Whiteboard.Start",
            $"screens={_displays.Count} argb=0x{_argb:X8} startedIn={sw.Elapsed.TotalMilliseconds:F1}ms");
    }

    public void Dispose() => _overlay.Dispose();

    // —————————————————— 内部 ——————————————————

    /// <summary>capture 回调:生成该屏纯色"冻结帧"(非预乘 BGRA;首行填充后逐行 memcpy)。</summary>
    private NativeBitmap MakeSolidFrame(DisplayInfo display)
    {
        var bmp = NativeBitmap.Allocate(display.Bounds.Width, display.Bounds.Height);
        FillSolid(bmp, _argb);
        return bmp;
    }

    private static unsafe void FillSolid(NativeBitmap bmp, int argb)
    {
        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        byte* p0 = (byte*)bmp.Scan0;
        int w = bmp.Width;
        for (int x = 0; x < w; x++)
        {
            byte* px = p0 + (long)x * 4;
            px[0] = b; px[1] = g; px[2] = r; px[3] = a;
        }
        long rowBytes = (long)w * 4;
        for (int y = 1; y < bmp.Height; y++)
            Buffer.MemoryCopy(p0, p0 + (long)y * bmp.Stride, rowBytes, rowBytes);
    }

    /// <summary>确认:SnipResult.Image(主屏全幅+标注)→ PNG。事件在内部线程触发。</summary>
    private void OnOverlayConfirmed(SnipResult result)
    {
        var sw = Stopwatch.StartNew();
        byte[]? png = null;
        try
        {
            png = BitmapConversion.EncodePng(result.Image);
        }
        catch (Exception ex)
        {
            PerfLog.Log("Whiteboard.Confirm", $"png encode FAILED: {ex.Message}");
        }
        PerfLog.Log("Whiteboard.Confirm",
            $"png bytes={png?.Length ?? 0} in {sw.Elapsed.TotalMilliseconds:F1}ms rect={result.PhysicalRect.Width}x{result.PhysicalRect.Height}");
        Confirmed?.Invoke(png);
    }
}
