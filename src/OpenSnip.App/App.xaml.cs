using System.Windows;
using System.Windows.Threading;
using OpenSnip.App.CommandLine;
using OpenSnip.App.Interop;
using OpenSnip.App.Ipc;
using OpenSnip.App.Services;
using OpenSnip.Core;

namespace OpenSnip.App;

/// <summary>
/// WPF 入口:命令行解析 → 单实例仲裁(命名管道 OpenSnip.Ipc,§3.4)。
/// 第二实例:完整命令行转发给首实例后退出;--block 等待执行完成回写(60s);
/// 首实例:装配托盘/热键/IPC/预热,常驻托盘。
/// </summary>
public partial class App : Application
{
    /// <summary>托管入口时刻(PerfLog 全链路起点,近似"进程启动")。</summary>
    internal static readonly long StartTickCount = Environment.TickCount64;

    private AppOrchestrator? _orchestrator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        NativeMethods.TryAttachParentConsole(); // 从 cmd 启动时回显 CLI 输出;资源管理器启动则静默失败

        var args = e.Args;
        var options = CommandLineParser.Parse(args);
        if (!options.Ok)
        {
            Console.Error.WriteLine($"[OpenSnip] 参数错误:{options.Error}");
            Console.Error.WriteLine(CommandLineParser.UsageText);
            PerfLog.Log("App", $"cli:usage-error {options.Error}");
            Shutdown(2);
            return;
        }

        // —— 测试模式(§8.2):跳过单实例仲裁,可与用户桌面实例共存;不启动 OpenSnip.Ipc ——
        if (options.TestRpc)
        {
            PerfLog.Log("App", "test-rpc:mode-enter(跳过单实例仲裁)");
        }
        else
        {
            // —— 第二实例路径:完整命令行转发给首实例(§3.4)——
            var relay = await IpcClient.RelayAsync(args, options.Block);
            if (relay.Connected)
            {
                PerfLog.Log("App", $"ipc:relayed-to-first-instance block={options.Block} status={relay.Status} ok={relay.Ok} msg={relay.Message}");
                if (options.Block)
                {
                    Console.WriteLine(relay.Ok
                    ? (string.IsNullOrEmpty(relay.Message) ? "[OpenSnip] OK" : "[OpenSnip] OK " + relay.Message)
                    : $"[OpenSnip] ERR {relay.Message}");
                }
                Shutdown(relay.ExitCode);
                return;
            }

            // —— 首实例路径 ——
            if (options.Command == CliCommandKind.Exit)
            {
                PerfLog.Log("App", "exit:no-running-instance(没有已运行实例,直接退出)");
                Shutdown(0);
                return;
            }
        }

        PerfLog.Log("App", $"startup:begin process-ms={Environment.TickCount64 - StartTickCount} args=[{string.Join(' ', args)}]");
        _orchestrator = new AppOrchestrator(testMode: options.TestRpc);
        try
        {
            await _orchestrator.InitializeAsync();
            PerfLog.Log("App", $"startup:app-ready(进程启动→装配完成) process-ms={Environment.TickCount64 - StartTickCount}");

            if (options.Command != CliCommandKind.None)
            {
                var (ok, err) = await _orchestrator.ExecuteAsync(options, "cli-startup");
                Console.WriteLine(ok
                    ? (string.IsNullOrEmpty(err) ? "[OpenSnip] OK" : "[OpenSnip] OK " + err)
                    : $"[OpenSnip] ERR {err}");
            }
        }
        catch (Exception ex)
        {
            PerfLog.Log("App", $"startup:failed {ex}");
            Console.Error.WriteLine($"[OpenSnip] 启动失败:{ex.Message}");
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs ex)
    {
        PerfLog.Log("App", $"ui:unhandled-exception {ex.Exception}");
        try
        {
            MessageBox.Show($"发生未处理异常,已忽略:\n{ex.Exception}", "OpenSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 无交互会话时静默 */ }
        ex.Handled = true; // 托盘常驻应用:吞掉 UI 线程异常继续运行
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _orchestrator?.Dispose(); } catch { /* 退出路径 */ }
        base.OnExit(e);
        NativeMethods.TryFreeConsole();
    }
}
