using System.Diagnostics;
using OpenSnip.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenSnip.Core.Tests;

/// <summary>
/// 截屏服务冒烟:真实捕获主显示器(允许 DXGI 或 BitBlt 任意路径成功)。
/// 不弹 UI、不写剪贴板。性能数据经 PerfLog 落盘供人工核对。
/// 内容有效性断言:非黑占比 > 0(黑帧回归防线)。
/// </summary>
public class ScreenCaptureTests
{
    private readonly ITestOutputHelper _output;

    public ScreenCaptureTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Capture_PrimaryDisplay_ReturnsMatchingDimensions()
    {
        using var service = new ScreenCaptureService();
        service.Warmup();

        var display = DisplayManager.Instance.Displays[0];
        var sw = Stopwatch.StartNew();
        using var bmp = service.Capture(display);
        sw.Stop();

        Assert.Equal(display.Bounds.Width, bmp.Width);
        Assert.Equal(display.Bounds.Height, bmp.Height);
        Assert.True(
            service.LastUsedMode is CaptureMode.DxgiDuplication or CaptureMode.GdiBitBlt,
            "LastUsedMode 必须是契约枚举值之一。");
        Assert.NotEqual(IntPtr.Zero, bmp.Scan0);

        PerfLog.Log("Core.Tests",
            $"Capture display={display.Index} {bmp.Width}x{bmp.Height} " +
            $"{sw.Elapsed.TotalMilliseconds:F2} ms mode={service.LastUsedMode} dxgi可用={service.DxgiAvailable}");
    }

    [Fact]
    public void Capture_SecondFrame_ReusesOrFallsBack_Stable()
    {
        using var service = new ScreenCaptureService();
        service.Warmup();
        var display = DisplayManager.Instance.Displays[0];

        using (var first = service.Capture(display))
        using (var second = service.Capture(display))
        {
            Assert.Equal(display.Bounds.Width, second.Width);
            Assert.Equal(first.Width, second.Width);
        }
    }

    [Fact]
    public void Capture_Repeated30Frames_PerfStatsToPerfLog()
    {
        using var service = new ScreenCaptureService();

        var displays = DisplayManager.Instance.Displays;
        foreach (var display in displays)
        {
            PerfLog.Log("Core.Tests",
                $"显示器清单 display={display.Index} {display.DeviceName} " +
                $"bounds={display.Bounds.X},{display.Bounds.Y} {display.Bounds.Width}x{display.Bounds.Height} " +
                $"primary={display.IsPrimary} dpi={display.DpiScale} hdr={display.IsHdr}");

            service.Warmup();
            var samples = new double[30];
            CaptureMode mode = default;
            for (int i = 0; i < samples.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                var bmp = service.Capture(display);
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
                mode = service.LastUsedMode;
                bmp.Dispose();
            }
            Array.Sort(samples);
            PerfLog.Log("Core.Tests",
                $"性能统计 display={display.Index} {display.Bounds.Width}x{display.Bounds.Height} " +
                $"mode={mode} n=30 min={samples[0]:F2} median={samples[samples.Length / 2]:F2} " +
                $"p90={samples[(int)(samples.Length * 0.9)]:F2} max={samples[^1]:F2} ms");
            Assert.All(samples, ms => Assert.True(ms < 5000, $"单帧 {ms:F0}ms 异常"));
        }
    }

    [Fact]
    public void Capture_Dxgi_HasRealContent()
    {
        using var service = new ScreenCaptureService();
        service.Warmup();
        if (!service.DxgiAvailable)
        {
            _output.WriteLine("环境无 DD 会话,跳过 DXGI 内容断言。");
            return; // 环境守卫:本机 DD 可用,正常执行
        }
        var display = DisplayManager.Instance.Displays[0];
        using var bmp = service.Capture(display);

        Assert.Equal(CaptureMode.DxgiDuplication, service.LastUsedMode);
        _output.WriteLine($"[黑帧回归探针] DD 捕获 {bmp.Width}x{bmp.Height} 非黑像素占比 = {service.LastContentRatio:P2}");
        Assert.True(service.LastContentRatio > 0, $"DXGI 帧疑似全黑,非黑占比={service.LastContentRatio:P2}");
    }

    [Fact]
    public void GdiCapture_HasRealContent_Baseline()
    {
        var display = DisplayManager.Instance.Displays[0];
        using var bmp = GdiCapture.Capture(display);
        double ratio = ContentProbe.NonBlackRatio(bmp);
        _output.WriteLine($"[黑帧回归探针] BitBlt(GDI 基准对照) {bmp.Width}x{bmp.Height} 非黑像素占比 = {ratio:P2}");
        Assert.True(ratio > 0, $"GDI 帧全黑,非黑占比={ratio:P2}(基准对照失败)");
    }

    [Fact]
    public void Capture_FullScreen_PngOnDisk_ForLeadVerification()
    {
        using var service = new ScreenCaptureService();
        service.Warmup();
        var display = DisplayManager.Instance.Displays[0];
        using var bmp = service.Capture(display);
        byte[] png = BitmapConversion.EncodePng(bmp);
        string dir = Path.GetDirectoryName(PerfLog.CurrentLogPath)!;
        string path = Path.Combine(dir, "verify-full-core.png");
        File.WriteAllBytes(path, png);
        _output.WriteLine(
            $"[实截验收] {bmp.Width}x{bmp.Height} PNG {png.Length:N0} B " +
            $"非黑占比={service.LastContentRatio:P2} mode={service.LastUsedMode} → {path}");
        Assert.True(service.LastContentRatio > 0, $"实截帧全黑,非黑占比={service.LastContentRatio:P2}");
        Assert.True(png.Length > 20_000, $"PNG 仅 {png.Length} B,疑似纯色/黑帧");
    }
}
