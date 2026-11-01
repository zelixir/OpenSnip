using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using OpenSnip.App.CommandLine;
using OpenSnip.App.Ipc;
using OpenSnip.Core;
using OpenSnip.Pin;

namespace OpenSnip.App.Services;

/// <summary>
/// App 壳总装配(SPEC §3.4):托盘 / F1·F3 热键 / 单实例命名管道 IPC / 启动预热 / 命令执行 / 退出。
/// </summary>
public sealed class AppOrchestrator : IDisposable
{
    private readonly StaWorker _sta = StaWorker.StartNew();
    private readonly Dispatcher _uiDispatcher;          // 构造于 WPF UI 线程
    private readonly OutputRunner _outputs;
    private readonly SnipService _snip;
    private readonly PasteService _paste;
    private readonly TrayService _tray = new();
    private readonly IpcServer _ipc;
    private readonly PinRegistry _pins = new();
    private readonly AppConfig _config;
    private readonly HistoryStore _history;
    private readonly HotCornerAdapter _hotCorners;
    private readonly TestRpcService _testRpc;
    private readonly bool _testMode;   // §8.2/§8.4:--test-rpc 测试模式
    private GlobalHotkeyManager? _hotkeys;
    private bool _exiting;

    public AppOrchestrator(bool testMode = false)
    {
        _testMode = testMode;
        _config = AppConfig.Load(); // §12:config.ini([Output]/[HotCorners];首次运行写默认模板)
        _history = new HistoryStore(AppPaths.HistoryDirectory);
        _uiDispatcher = Dispatcher.CurrentDispatcher;   // OnStartup 在 UI 线程 new 本类
        _outputs = new OutputRunner(_sta, _pins, _config, _history);
        _snip = new SnipService(_sta, _outputs, testMode);
        _paste = new PasteService(_sta, _pins);
        _ipc = new IpcServer(HandleIpcCommandAsync);
        // §12 触发角:Core.HotCornerMonitor 经适配层接入(core-dev 产物就绪即生效)
        _hotCorners = HotCornerAdapter.Create(TimeSpan.FromMilliseconds(_config.DwellMs));
        // §8.2 测试 RPC:snip.interactive / 遮罩驱动 / pins / tray 状态源 / paste / history / corners(仅 --test-rpc 模式 Start)
        _testRpc = new TestRpcService(
            targets => _snip.StartInteractiveAsync("test-rpc", targets),
            () => _snip.TryGetActiveOverlay(),
            _pins,
            () => _tray.IsReady,
            () => _paste.PinFromClipboard("test-rpc"),
            _history,
            CornerSet);
    }

    /// <summary>§12 corners.set 的实现:运行时开关(需 Core.HotCornerMonitor 就绪)。</summary>
    private (bool Ok, string? Error) CornerSet(bool enabled)
    {
        if (!_hotCorners.Available)
        {
            return (false, "HotCornerMonitor 未就绪:" + _hotCorners.UnavailableReason);
        }
        _hotCorners.Enabled = enabled;
        PerfLog.Log("App", $"hotcorner:enabled={enabled}");
        return (true, null);
    }

    /// <summary>§12 触发角动作派发(钩子线程,轻量转交)。</summary>
    private void OnCornerEntered(int corner)
    {
        var action = corner >= 0 && corner < 4 ? _config.CornerActions[corner] : "无";
        PerfLog.Log("App", $"hotcorner:enter corner={corner} action={action}");
        switch (action)
        {
            case "snip": _ = StartInteractiveSafe("hotcorner" + corner); break;
            case "paste": Task.Run(() => _paste.PinFromClipboard("hotcorner" + corner)); break;
            case "show-images": _pins.SetPinsOpacityAll(100); break;
            case "hide-images": _pins.SetPinsOpacityAll(10); break;
            default: break; // 无
        }
    }

    /// <summary>装配托盘/热键/IPC/预热(UI 线程调用)。PerfLog:进程启动→托盘就绪全链路。</summary>
    public Task InitializeAsync()
    {
        var sw = Stopwatch.StartNew();

        _tray.SnipRequested += () => _ = StartInteractiveSafe("tray");
        _tray.PasteRequested += () => Task.Run(() => _paste.PinFromClipboard("tray"));
        _tray.ExitRequested += () => RequestExit("tray");
        _tray.Initialize(suppressErrors: _testMode); // 测试模式(隐藏桌面可能无任务栏)容忍托盘创建失败

        _hotkeys = new GlobalHotkeyManager();
        // F1=VK_F1(0x70)截屏,F3=VK_F3(0x72)贴图;无修饰键。事件在热键线程触发 ⇒ 订阅内自行调度
        bool f1 = _hotkeys.Register(1, 0, 0x70);
        bool f3 = _hotkeys.Register(2, 0, 0x72);
        _hotkeys.HotkeyPressed += id =>
        {
            switch (id)
            {
                case 1: _ = StartInteractiveSafe("hotkey"); break;
                case 2: Task.Run(() => _paste.PinFromClipboard("hotkey")); break;
            }
        };

        if (_testMode)
        {
            // §8.2:--test-rpc 不启动 OpenSnip.Ipc(可与用户桌面实例共存),改开测试管道
            _testRpc.Start();
            PerfLog.Log("App", $"startup:test-rpc-listening pipe={TestRpcService.PipeName} init={sw.Elapsed.TotalMilliseconds:F1}ms");
        }
        else
        {
            _ipc.Start();
            PerfLog.Log("App", $"startup:ipc-listening pipe={IpcProtocol.PipeName} init={sw.Elapsed.TotalMilliseconds:F1}ms");
        }

        if (_testMode)
        {
            // §8.4:底图注入模式不创建截屏服务,预热无意义(隐藏桌面 DD 也不可用)
            PerfLog.Log("App", "warmup:skipped(test-rpc 底图注入模式)");
        }
        else
        {
            new WarmupService(() => _snip.EnsureCapture()).Schedule(3000); // §3.4:启动 3s 后预热
        }

        // §12 触发角:就绪则按配置启用
        if (_hotCorners.Available)
        {
            _hotCorners.CornerEntered += OnCornerEntered;
            _hotCorners.Enabled = _config.HotCornersEnabled;
            PerfLog.Log("App", $"hotcorner:init available=True enabled={_config.HotCornersEnabled} dwell={_config.DwellMs}ms actions=[{string.Join(',', _config.CornerActions)}]");
        }
        else
        {
            PerfLog.Log("App", $"hotcorner:init available=False reason={_hotCorners.UnavailableReason}");
        }
        PerfLog.Log("App", $"config:loaded autosave={_config.AutoSave} outDir={(string.IsNullOrEmpty(_config.OutputDir) ? "(默认)" : _config.OutputDir)} historyDir={_history.Directory}");

        PerfLog.Log("App", $"startup:ready(托盘/热键/IPC·TestRpc/预热已安排) init={sw.Elapsed.TotalMilliseconds:F1}ms f1={f1} f3={f3} test={_testMode} process-ms={AppClock.ElapsedMs}");
        return Task.CompletedTask;
    }

    private async Task StartInteractiveSafe(string trigger)
    {
        var (ok, err) = await _snip.StartInteractiveAsync(trigger, DefaultInteractiveOutputs());
        if (!ok && err != "已取消")
        {
            PerfLog.Log("App", $"snip:interactive-rejected trigger={trigger} err={err}");
        }
    }

    /// <summary>交互截屏未显式给 -o 时的默认输出=剪贴板(验收 §5.2:Enter 后画图 Ctrl+V 可粘贴)。</summary>
    public static IReadOnlyList<OutputTarget> DefaultInteractiveOutputs() =>
        new OutputTarget[] { new(OutputTargetKind.Clipboard) };

    private async Task<(bool Ok, string? Error)> HandleIpcCommandAsync(IReadOnlyList<string> args)
    {
        var options = CommandLineParser.Parse(args);
        if (!options.Ok)
        {
            return (false, options.Error);
        }
        return await ExecuteAsync(options, "ipc");
    }

    /// <summary>执行解析后的命令;结果回给 IPC 客户端(--block)或首实例启动参数。任何线程可调。</summary>
    public async Task<(bool Ok, string? Error)> ExecuteAsync(CliOptions options, string source)
    {
        switch (options.Command)
        {
            case CliCommandKind.Snip:
            {
                var targets = options.HasExplicitOutputs ? options.Outputs : DefaultInteractiveOutputs();
                return options.Mode switch
                {
                    SnipMode.Full => await _snip.CaptureDirectAsync(SnipMode.Full, 0, 0, 0, 0, targets, options.DelaySeconds),
                    SnipMode.Area => await _snip.CaptureDirectAsync(SnipMode.Area, options.AreaX, options.AreaY, options.AreaW, options.AreaH, targets, options.DelaySeconds),
                    _ => await _snip.StartInteractiveAsync(source, targets, options.DelaySeconds),
                };
            }
            case CliCommandKind.Paste:
                return await Task.Run(() => _paste.PinFromClipboard(source));
            case CliCommandKind.History:
            {
                // §12:history [N] — 单行摘要回给客户端(回放 UI 为 M2b)
                var entries = _history.List(options.HistoryCount);
                var parts = entries.Select((e, i) => $"{i + 1}) {e.File} {e.Time} {e.W}x{e.H}").ToList();
                var summary = $"total={_history.Total} show={entries.Count} :: " + string.Join("  ", parts);
                return (true, summary);
            }
            case CliCommandKind.Exit:
                RequestExit(source);
                return (true, null);
            default:
                return (true, null); // 无命令:正常常驻启动
        }
    }

    /// <summary>退出:贴图全部关闭 → 热键/托盘/IPC/截屏服务释放 → WPF Shutdown(0)。</summary>
    public void RequestExit(string source)
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        PerfLog.Log("App", $"exit:begin source={source} process-ms={AppClock.ElapsedMs}");
        try { PinManager.Shutdown(); }
        catch (Exception ex) { PerfLog.Log("App", $"exit:pin-shutdown-failed {ex.Message}"); }
        try { _hotkeys?.Dispose(); } catch { /* 已释放 */ }
        try { _tray.Dispose(); } catch { /* 已释放 */ }
        _ = _ipc.DisposeAsync();
        _ = _testRpc.DisposeAsync();
        try { _hotCorners.Dispose(); } catch { /* 已释放 */ }

        _uiDispatcher.BeginInvoke(() =>
        {
            PerfLog.Log("App", "exit:shutdown-dispatched");
            Application.Current?.Shutdown(0);
        });
    }

    public void Dispose()
    {
        try { _snip.Dispose(); } catch { /* 已释放 */ }
        try { _hotkeys?.Dispose(); } catch { /* 已释放 */ }
        try { _tray.Dispose(); } catch { /* 已释放 */ }
        try { _ipc.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { /* 退出路径 */ }
        try { _testRpc.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { /* 退出路径 */ }
        try { _hotCorners.Dispose(); } catch { /* 退出路径 */ }
        _sta.Dispose();
    }
}
