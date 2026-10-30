using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using OpenSnip.App.CommandLine;
using OpenSnip.Core;
using OpenSnip.Pin;

namespace OpenSnip.App.Services;

/// <summary>
/// 截图输出执行器:按 SPEC §3.4 顺序 FILE → clipboard → pin,在 STA 工作线程上执行。
/// pin 目标存在时位图所有权转移给 PinWindow(其 Close 时 Dispose,契约 §2.9),否则由调用方负责释放。
/// </summary>
public sealed class OutputRunner
{
    private readonly StaWorker _sta;

    public OutputRunner(StaWorker sta) => _sta = sta;

    /// <summary>执行全部输出;返回 pin 是否接管了位图(接管 ⇒ 调用方不得再 Dispose)。</summary>
    public bool Run(NativeBitmap image, PxRect sourceRect, IReadOnlyList<OutputTarget> targets)
        => _sta.Invoke(() => RunCore(image, sourceRect, targets));

    private bool RunCore(NativeBitmap image, PxRect sourceRect, IReadOnlyList<OutputTarget> targets)
    {
        bool pinTaken = false;

        // ① 文件输出(含 -o save 弹对话框)
        foreach (var t in targets.Where(t => t.Kind is OutputTargetKind.File or OutputTargetKind.SaveDialog))
        {
            SaveToFile(image, t);
        }

        // ② 剪贴板
        if (targets.Any(t => t.Kind == OutputTargetKind.Clipboard))
        {
            var sw = Stopwatch.StartNew();
            var ok = ClipboardWriter.PutImage(image); // 契约 §2.6:CF_DIB + "PNG" 双格式;STA 线程
            PerfLog.Log("App", $"output:clipboard ok={ok} {sw.Elapsed.TotalMilliseconds:F1}ms");
        }

        // ③ 贴图:选区来源则贴回原位(sourceRect=原选区/直截矩形),否则主屏中心(§2.9 调用约定)
        if (targets.Any(t => t.Kind == OutputTargetKind.Pin))
        {
            var sw = Stopwatch.StartNew();
            var rect = sourceRect.IsEmpty
                ? PasteService.CenterIn(PasteService.GetPrimaryWorkArea(), image.Width, image.Height)
                : sourceRect;
            PinManager.Pin(image, rect); // 所有权转移:PinWindow Close 时 Dispose(§2.9)
            pinTaken = true;
            PerfLog.Log("App", $"output:pin rect={rect.X},{rect.Y} {rect.Width}x{rect.Height} {sw.Elapsed.TotalMilliseconds:F1}ms");
        }
        return pinTaken;
    }

    private void SaveToFile(NativeBitmap image, OutputTarget target)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var path = target.Kind == OutputTargetKind.SaveDialog ? AskSavePath() : target.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                PerfLog.Log("App", "output:save-cancelled");
                return;
            }
            // -o 给的是已存在目录时,写入默认名文件(SPEC §3.4 默认文件名)
            if (Directory.Exists(path))
            {
                path = System.IO.Path.Combine(path, DefaultFileName());
            }
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var png = BitmapConversion.EncodePng(image); // 契约 API:压缩 PNG 字节流(红线 §0.4 的明示例外)
            File.WriteAllBytes(path, png);
            PerfLog.Log("App", $"output:file {path} {sw.Elapsed.TotalMilliseconds:F1}ms");
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"output:file-failed target={target.Path} {ex.Message}");
        }
    }

    /// <summary>SaveFileDialog(WPF 内置,§3.4 保存条目),默认名 Snip_yyyy-MM-dd_HH-mm-ss.png。</summary>
    private string? AskSavePath()
    {
        var dlg = new SaveFileDialog
        {
            FileName = DefaultFileName(),
            DefaultExt = ".png",
            Filter = "PNG 图片|*.png|所有文件|*.*",
            OverwritePrompt = true,
            Title = "保存截屏",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    /// <summary>SPEC §3.4 默认保存文件名。</summary>
    public static string DefaultFileName() => $"Snip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
}
