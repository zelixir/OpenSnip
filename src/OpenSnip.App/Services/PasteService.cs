using System.Diagnostics;
using OpenSnip.Core;
using OpenSnip.Pin;

namespace OpenSnip.App.Services;

/// <summary>
/// 贴图流程(F3 / paste 命令):读剪贴板图像 → PinManager.Pin 到主屏中心(SPEC §3.4)。
/// 来源为剪贴板(非选区),故按 §2.9 调用约定贴主屏中心而非原位。
/// </summary>
public sealed class PasteService
{
    private readonly StaWorker _sta;
    private readonly PinRegistry _pins;

    public PasteService(StaWorker sta, PinRegistry pins)
    {
        _sta = sta;
        _pins = pins;
    }

    public (bool Ok, string? Error) PinFromClipboard(string trigger)
    {
        var sw = Stopwatch.StartNew();
        PerfLog.Log("App", $"paste:begin trigger={trigger}");
        var images = _sta.Invoke(ClipboardReader.ReadImages); // STA 线程读剪贴板
        if (images.Count == 0)
        {
            PerfLog.Log("App", $"paste:no-image {sw.Elapsed.TotalMilliseconds:F1}ms");
            return (false, "剪贴板中没有可贴的图像(支持 CF_DIB / PNG / 图片文件路径)");
        }
        PerfLog.Log("App", $"paste:clipboard-read count={images.Count} {sw.Elapsed.TotalMilliseconds:F1}ms");

        var area = GetPrimaryWorkArea();
        int cascade = 0;
        foreach (var bmp in images)
        {
            var rect = CenterIn(area, bmp.Width, bmp.Height, cascade);
            _pins.Track(PinManager.Pin(bmp, rect)); // 所有权转移(§2.9);登记供 §8.2 status
            PerfLog.Log("App", $"paste:pin rect={rect.X},{rect.Y} {rect.Width}x{rect.Height}");
            cascade++;
        }
        return (true, null);
    }

    /// <summary>主屏工作区(物理 px);枚举失败兜底 1920×1080。</summary>
    public static PxRect GetPrimaryWorkArea()
    {
        var displays = DisplayManager.Instance.Displays;
        var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        return primary?.WorkArea ?? new PxRect(0, 0, 1920, 1080);
    }

    /// <summary>在 area 内居中放置 w×h;cascade&gt;0 时向右下错开避免多图完全重叠;越界夹回区域。</summary>
    public static PxRect CenterIn(PxRect area, int w, int h, int cascade = 0)
    {
        int x = area.X + (area.Width - w) / 2 + cascade * 32;
        int y = area.Y + (area.Height - h) / 2 + cascade * 32;
        if (w < area.Width)
        {
            x = Math.Clamp(x, area.X, area.X + area.Width - w);
        }
        else
        {
            x = area.X;
        }
        if (h < area.Height)
        {
            y = Math.Clamp(y, area.Y, area.Y + area.Height - h);
        }
        else
        {
            y = area.Y;
        }
        return new PxRect(x, y, w, h);
    }
}
