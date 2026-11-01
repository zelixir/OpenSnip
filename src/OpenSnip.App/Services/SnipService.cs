using System.Diagnostics;
using System.IO;
using OpenSnip.App.CommandLine;
using OpenSnip.App.Interop;
using OpenSnip.Core;
using OpenSnip.Overlay;

namespace OpenSnip.App.Services;

/// <summary>
/// 截屏流程(SPEC §3.4):交互遮罩(SnipOverlay→Confirmed→剪贴板/存文件/Pin)与 --full/--area 直截。
/// PerfLog 全链路打点:触发→冻结→遮罩→确认→各类输出完成。
/// </summary>
public sealed class SnipService : IDisposable
{
    private readonly object _gate = new();
    private readonly StaWorker _sta;
    private readonly OutputRunner _outputs;
    private ScreenCaptureService? _capture;     // 全 App 生命周期一份,预热后复用 DD 会话
    private SnipOverlay? _activeOverlay;
    private bool _busy;
    private readonly HistoryStore _history;     // §19 回放帧来源(§12 历史存档)

    private readonly bool _testMode;            // §8.4:--test-rpc 底图注入开关

    /// <summary>§19 回放帧数上限(默认 10)。</summary>
    public const int ReplayMax = 10;

    public SnipService(StaWorker sta, OutputRunner outputs, bool testMode = false, HistoryStore? history = null)
    {
        _sta = sta;
        _outputs = outputs;
        _testMode = testMode;
        _history = history ?? new HistoryStore(AppPaths.HistoryDirectory);
    }

    // —— 交互式截屏(F1 / 托盘 / CLI snip)——

    /// <summary>异步开始交互截屏;忙时拒绝(防 F1 重入)。遮罩期间不阻塞调用方(等待方 await 返回值)。</summary>
    public Task<(bool Ok, string? Error)> StartInteractiveAsync(
        string trigger, IReadOnlyList<OutputTarget> targets, double delaySeconds = 0)
    {
        lock (_gate)
        {
            if (_busy)
            {
                PerfLog.Log("App", $"snip:ignored-busy trigger={trigger}");
                return Task.FromResult<(bool Ok, string? Error)>((false, "已有截屏正在进行中"));
            }
            _busy = true;
        }
        PerfLog.Log("App", $"snip:interactive-begin trigger={trigger} t={AppClock.ElapsedMs}ms");
        var metaCtx = NativeMethods.GetForegroundContext(); // §12:截屏瞬间前台窗口($title$/$exe$)
        var meta = new SnipMeta(metaCtx.Title, metaCtx.Exe);
        return Task.Run(() => RunInteractiveAsync(trigger, targets, delaySeconds, meta));
    }

    private async Task<(bool Ok, string? Error)> RunInteractiveAsync(
        string trigger, IReadOnlyList<OutputTarget> targets, double delaySeconds, SnipMeta meta)
    {
        var total = Stopwatch.StartNew();
        try
        {
            if (delaySeconds > 0)
            {
                PerfLog.Log("App", $"snip:delay {delaySeconds}s");
                Thread.Sleep((int)(delaySeconds * 1000));
            }

            var displays = DisplayManager.Instance.Displays;
            if (displays.Count == 0)
            {
                return (false, "未枚举到任何显示器");
            }
            var capture = _testMode ? null : EnsureCapture(); // §8.4:底图注入时不创建截屏服务

            // §19 历史回放:读最近 N 帧(App 拥有位图,会话结束统一释放)
            var historyFrames = LoadReplayFrames();
            PerfLog.Log("App", $"snip:replay-frames count={historyFrames.Count} decode-total={total.Elapsed.TotalMilliseconds:F1}ms");

            var overlay = new SnipOverlay(displays, display => CaptureForSnip(capture, display), historyFrames);

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            overlay.Confirmed += result =>
            {
                try
                {
                    PerfLog.Log("App", $"snip:confirmed rect={result.PhysicalRect.X},{result.PhysicalRect.Y} {result.PhysicalRect.Width}x{result.PhysicalRect.Height} display={result.Display.Index} total={total.Elapsed.TotalMilliseconds:F1}ms");
                    // 选区来源 ⇒ 贴回原位(result.PhysicalRect);pin 存在则所有权转移给 PinWindow(§2.9)
                    bool pinned = _outputs.Run(result.Image, result.PhysicalRect, targets, meta);
                    if (!pinned)
                    {
                        result.Image.Dispose(); // 无 pin 输出:App 负责释放
                    }
                    PerfLog.Log("App", $"snip:outputs-done total={total.Elapsed.TotalMilliseconds:F1}ms");
                }
                catch (Exception ex)
                {
                    PerfLog.Log("App", $"snip:outputs-failed {ex}");
                    result.Image.Dispose();
                }
                finally
                {
                    done.TrySetResult(true);
                }
            };
            overlay.Cancelled += () =>
            {
                PerfLog.Log("App", $"snip:cancelled total={total.Elapsed.TotalMilliseconds:F1}ms");
                done.TrySetResult(false);
            };

            lock (_gate) { _activeOverlay = overlay; }
            overlay.Start();
            PerfLog.Log("App", $"snip:overlay-start-returned {total.Elapsed.TotalMilliseconds:F1}ms");

            var confirmed = await done.Task; // 事件在遮罩内部线程触发后落定(Close() 亦视同取消)
            return (confirmed, confirmed ? null : "已取消");
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"snip:error {ex}");
            return (false, ex.Message);
        }
        finally
        {
            lock (_gate) { _activeOverlay = null; _busy = false; }
            lock (_replayGate)
            {
                foreach (var f in _replayFrames)
                {
                    try { f.Dispose(); } catch { /* 双释放防护在 NativeBitmap 内 */ }
                }
                _replayFrames.Clear();
            }
            PerfLog.Log("App", $"snip:end total={total.Elapsed.TotalMilliseconds:F1}ms");
        }
    }

    // —— §19 历史回放帧 ——
    private readonly object _replayGate = new();
    private readonly List<NativeBitmap> _replayFrames = new();

    /// <summary>
    /// §19:读最近 ReplayMax 帧(默认 10)并解码为 NativeBitmap(展平:单屏即帧序);
    /// 最新历史在前(帧 1=最近一次)。并行解码,失败条目跳过。App 拥有位图,会话结束释放。
    /// </summary>
    private List<NativeBitmap> LoadReplayFrames()
    {
        var result = new List<NativeBitmap>();
        try
        {
            var entries = _history.List(ReplayMax);
            if (entries.Count == 0)
            {
                return result;
            }
            var paths = entries
                .Select(e => System.IO.Path.Combine(_history.Directory, e.File))
                .Where(File.Exists)
                .ToList();
            var decoded = new NativeBitmap?[paths.Count];
            _ = Parallel.For(0, paths.Count, i =>
            {
                try
                {
                    using var fs = File.OpenRead(paths[i]);
                    decoded[i] = ImageDecoder.DecodeToNativeBitmap(fs);
                }
                catch (Exception ex)
                {
                    PerfLog.Log("App", $"snip:replay-decode-failed {paths[i]} {ex.Message}");
                }
            });
            foreach (var d in decoded)
            {
                if (d is not null)
                {
                    result.Add(d);
                }
            }
            lock (_replayGate)
            {
                _replayFrames.AddRange(result); // 会话结束统一释放(§19:App 拥有)
            }
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"snip:replay-error {ex.Message}");
        }
        return result;
    }

    // —— 直接截取(--full / --area,无遮罩)——

    public Task<(bool Ok, string? Error)> CaptureDirectAsync(
        SnipMode mode, int x, int y, int w, int h, IReadOnlyList<OutputTarget> targets, double delaySeconds)
    {
        var metaCtx = NativeMethods.GetForegroundContext();
        var meta = new SnipMeta(metaCtx.Title, metaCtx.Exe);
        return Task.Run(() => RunDirect(mode, x, y, w, h, targets, delaySeconds, meta));
    }

    private (bool Ok, string? Error) RunDirect(
        SnipMode mode, int x, int y, int w, int h, IReadOnlyList<OutputTarget> targets, double delaySeconds, SnipMeta meta)
    {
        var total = Stopwatch.StartNew();
        try
        {
            if (delaySeconds > 0)
            {
                PerfLog.Log("App", $"snip:delay {delaySeconds}s");
                Thread.Sleep((int)(delaySeconds * 1000));
            }

            var displays = DisplayManager.Instance.Displays;
            if (displays.Count == 0)
            {
                return (false, "未枚举到任何显示器");
            }
            var capture = _testMode ? null : EnsureCapture();

            DisplayInfo display;
            if (mode == SnipMode.Full)
            {
                display = displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];
            }
            else
            {
                var center = new PxPoint(x + w / 2, y + h / 2);
                display = DisplayManager.Instance.GetDisplayAt(center)
                    ?? DisplayManager.Instance.GetDisplayByIndex(0)
                    ?? throw new InvalidOperationException($"无法定位区域中心 {center.X},{center.Y} 所在显示器");
            }

            var swCapture = Stopwatch.StartNew();
            var shot = CaptureForSnip(capture, display);
            swCapture.Stop();
            PerfLog.Log("App", $"snip:direct-capture display={display.Index}({display.DeviceName}) {swCapture.Elapsed.TotalMilliseconds:F1}ms");

            NativeBitmap image;
            PxRect outRect;
            if (mode == SnipMode.Full)
            {
                image = shot;
                outRect = display.Bounds;
            }
            else
            {
                var clip = Intersect(new PxRect(x, y, w, h), display.Bounds);
                if (clip.IsEmpty)
                {
                    shot.Dispose();
                    return (false, $"区域 {x},{y} {w}x{h} 不与显示器 {display.DeviceName}({display.Bounds}) 相交");
                }
                // 契约 §2.2:CopyRegionFrom 按目标尺寸从冻结整屏取子区
                image = NativeBitmap.Allocate(clip.Width, clip.Height);
                try
                {
                    image.CopyRegionFrom(shot, clip.X - display.Bounds.X, clip.Y - display.Bounds.Y);
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
                finally
                {
                    shot.Dispose(); // 全屏中间产物即刻释放
                }
                outRect = clip;
            }

            bool pinned = _outputs.Run(image, outRect, targets, meta);
            if (!pinned)
            {
                image.Dispose();
            }
            PerfLog.Log("App", $"snip:direct-done mode={mode} rect={outRect.X},{outRect.Y} {outRect.Width}x{outRect.Height} total={total.Elapsed.TotalMilliseconds:F1}ms");
            return (true, null);
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"snip:direct-error {ex}");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 冻结画面统一入口:测试模式返回 §8.4 的 8 色带合成底图(隐藏桌面 DD/BitBlt 均不可用,实测裁决),
    /// 正常模式走 ScreenCaptureService(DXGI 失败自动回退 BitBlt,§2.3)。
    /// 恢复测试模式真实截屏(如在可见测试桌面对拍):构造 SnipService 传 testMode:false 即可。
    /// </summary>
    private NativeBitmap CaptureForSnip(ScreenCaptureService? capture, DisplayInfo display)
    {
        if (_testMode)
        {
            var pattern = TestBaseImage.Create(display.Bounds);
            PerfLog.Log("App", $"snip:freeze TEST-PATTERN(§8.4 底图注入) display={display.Index} {pattern.Width}x{pattern.Height} bands=8 t={AppClock.ElapsedMs}ms");
            return pattern;
        }
        var bmp = capture!.Capture(display); // DXGI 失败自动回退 BitBlt(§2.3)
        PerfLog.Log("App", $"snip:freeze display={display.Index} {bmp.Width}x{bmp.Height} mode={capture.LastUsedMode} t={AppClock.ElapsedMs}ms");
        return bmp;
    }

    /// <summary>物理像素矩形求交(不相交返回 PxRect.Empty)。</summary>
    private static PxRect Intersect(PxRect a, PxRect b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? new PxRect(left, top, right - left, bottom - top) : PxRect.Empty;
    }

    /// <summary>惰性创建截屏服务(预热与首截共用同一实例,复用 DD 会话)。</summary>
    internal ScreenCaptureService EnsureCapture()
    {
        lock (_gate)
        {
            return _capture ??= new ScreenCaptureService();
        }
    }

    /// <summary>当前活动遮罩(§8.2 测试 RPC 驱动用);无活动返回 null。Post* 调用由 Overlay 内部封送。</summary>
    internal SnipOverlay? TryGetActiveOverlay()
    {
        lock (_gate)
        {
            return _activeOverlay;
        }
    }

    /// <summary>§19 snip.setFrame:切回放帧(0=本次 capture;透传 Overlay 封送)。无活动遮罩返回 false。</summary>
    internal bool TryPostSetFrame(int index)
    {
        var overlay = TryGetActiveOverlay();
        if (overlay is null)
        {
            return false;
        }
        overlay.PostSetFrame(index);
        return true;
    }

    /// <summary>退出时若遮罩开着,强制关闭(视同取消,§2.8)。</summary>
    public void CloseActiveOverlay()
    {
        lock (_gate)
        {
            try { _activeOverlay?.Close(); } catch { /* Overlay 侧自兜底 */ }
        }
    }

    public void Dispose()
    {
        CloseActiveOverlay();
        lock (_gate)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }
}
