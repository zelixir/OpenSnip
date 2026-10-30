using System.Diagnostics;
using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>启动预热:启动 3s 后后台线程预加载 DisplayManager 并 Warmup 截屏服务(SPEC §3.4),消除首截抖动。</summary>
public sealed class WarmupService
{
    private readonly Func<ScreenCaptureService?> _captureProvider;

    public WarmupService(Func<ScreenCaptureService?> captureProvider)
        => _captureProvider = captureProvider;

    public void Schedule(int delayMilliseconds = 3000)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMilliseconds);
                var sw = Stopwatch.StartNew();
                var displays = DisplayManager.Instance.Displays; // 预加载显示器枚举(懒加载单例)
                var capture = _captureProvider();
                capture?.Warmup();
                PerfLog.Log("App", $"warmup:done displays={displays.Count} dxgi={capture?.DxgiAvailable.ToString() ?? "n/a"} {sw.Elapsed.TotalMilliseconds:F1}ms");
            }
            catch (Exception ex)
            {
                PerfLog.Log("App", $"warmup:failed {ex}");
            }
        });
    }
}
