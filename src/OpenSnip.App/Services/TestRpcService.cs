using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenSnip.App.CommandLine;
using OpenSnip.App.Ipc;
using OpenSnip.Core;
using OpenSnip.Overlay;

namespace OpenSnip.App.Services;

/// <summary>
/// M1.5 无头 E2E 测试 RPC(SPEC §8.2):命名管道 OpenSnip.TestRpc,JSON 单行请求 → 单行 JSON 响应。
/// 仅 --test-rpc 模式启动:跳过单实例仲裁、不启动 OpenSnip.Ipc,可与用户桌面实例共存;
/// RPC 服务线程独立,触 UI 走既有封送(SnipService 的 Task.Run / PinManager 线程队列),
/// 驱动遮罩走 Overlay 新 API SnipOverlay.PostSetRegion/PostConfirm/PostCancel(§8.1,其内部线程执行)。
/// </summary>
public sealed class TestRpcService : IAsyncDisposable
{
    /// <summary>§8.2 固定测试管道名。</summary>
    public const string PipeName = "OpenSnip.TestRpc";

    /// <summary>中文直出(仍为合法 JSON),perf.log/E2E 人读友好。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<IReadOnlyList<OutputTarget>, Task<(bool Ok, string? Error)>> _startSnip;
    private readonly Func<SnipOverlay?> _activeOverlay;
    private readonly PinRegistry _pins;
    private readonly Func<bool> _trayReady;
    private readonly Func<(bool Ok, string? Error)> _paste;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public TestRpcService(
        Func<IReadOnlyList<OutputTarget>, Task<(bool Ok, string? Error)>> startSnip,
        Func<SnipOverlay?> activeOverlay,
        PinRegistry pins,
        Func<bool> trayReady,
        Func<(bool Ok, string? Error)> paste)
    {
        _startSnip = startSnip;
        _activeOverlay = activeOverlay;
        _pins = pins;
        _trayReady = trayReady;
        _paste = paste;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private static NamedPipeServerStream CreatePipe() =>
        new(PipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private async Task LoopAsync(CancellationToken ct)
    {
        var pipe = CreatePipe();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    PerfLog.Log("App", $"test-rpc:wait-connection-failed {ex.Message}");
                    continue;
                }

                var next = CreatePipe(); // 提前备好下一实例,消除重连窗口
                bool cancelled = false;
                try
                {
                    await HandleSessionAsync(pipe, ct);
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex)
                {
                    PerfLog.Log("App", $"test-rpc:session-error {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* 客户端已断开 */ }
                    pipe.Dispose();
                }
                if (cancelled)
                {
                    next.Dispose();
                    return;
                }
                pipe = next;
            }
        }
        finally
        {
            pipe.Dispose();
        }
    }

    /// <summary>单会话处理多条命令(E2E 长连接轮询更稳);EOF/断开结束会话。</summary>
    private async Task HandleSessionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await IpcProtocol.ReadLineAsync(pipe, ct);
            if (line.Length == 0)
            {
                break; // EOF:客户端断开
            }
            PerfLog.Log("App", $"test-rpc:received {line}");
            string response;
            try
            {
                response = HandleRequest(line);
            }
            catch (Exception ex)
            {
                response = Err($"处理失败:{ex.Message}");
            }
            PerfLog.Log("App", $"test-rpc:respond {response}");
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), ct);
            await pipe.FlushAsync(ct);
        }
    }

    /// <summary>处理单条 JSON 命令(§8.2 协议);任何异常都折算为 {"ok":false,"error":…}。</summary>
    internal string HandleRequest(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("cmd", out var cmdEl)
            || cmdEl.ValueKind != JsonValueKind.String)
        {
            return Err("缺少字符串字段 cmd");
        }
        switch (cmdEl.GetString())
        {
            case "status":
                return Status();
            case "snip.interactive":
                return SnipInteractive(root);
            case "snip.setRegion":
                return SnipSetRegion(root);
            case "snip.confirm":
                return WithOverlay(static o => { o.PostConfirm(); return Ok(); }); // §8.1:等价 Enter
            case "snip.cancel":
                return WithOverlay(static o => { o.PostCancel(); return Ok(); }); // §8.1:等价 Esc
            case "pin.closeAll":
            {
                var n = _pins.CloseAll();
                PerfLog.Log("App", $"test-rpc:pin-closeAll closed={n}");
                return Ok();
            }
            case "paste":
            {
                // 与 CLI paste 同语义(§8.4 追加):读剪贴板图像 → 贴主屏工作区中心;所有权归 PinWindow
                var (ok, err) = _paste();
                return ok ? Ok() : Err(err ?? "贴图失败");
            }
            case "overlay.snapshot":
                return OverlaySnapshot(root);
            default:
                return Err($"未知命令:{cmdEl.GetString()}");
        }
    }

    /// <summary>
    /// overlay.snapshot(§8.5):进程内把活动遮罩渲染 DIB 编码 PNG 写 out 路径;响应 {ok,w,h}。
    /// 无活动遮罩/导出失败 → ok:false。
    /// </summary>
    private string OverlaySnapshot(JsonElement root)
    {
        if (!root.TryGetProperty("out", out var outEl) || outEl.ValueKind != JsonValueKind.String)
        {
            return Err("overlay.snapshot 需要字符串 out(PNG 路径)");
        }
        var path = Environment.ExpandEnvironmentVariables(outEl.GetString()!);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Err("out 为空");
        }
        var overlay = _activeOverlay();
        if (overlay is null)
        {
            return Err("无遮罩活动");
        }
        var snap = overlay.SnapshotPng();
        if (snap is null || snap.Value.Png is null)
        {
            return Err("快照导出失败(无窗口/超时)");
        }
        var (png, w, h) = snap.Value;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(path, png);
        PerfLog.Log("App", $"test-rpc:overlay-snapshot out={path} {w}x{h} bytes={png.Length}");
        return JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["ok"] = true, ["w"] = w, ["h"] = h }, JsonOpts);
    }

    /// <summary>遮罩驱动命令的公共壳:无活动遮罩 → ok:false(§8.2 setRegion/confirm 语义,cancel 同口径)。</summary>
    private string WithOverlay(Func<SnipOverlay, string> action)
    {
        var overlay = _activeOverlay();
        if (overlay is null)
        {
            return Err("无遮罩活动");
        }
        try
        {
            return action(overlay);
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    /// <summary>status(§8.2):pid/tray/overlay.active/overlay.windows(十进制字符串)/pins 列表。</summary>
    private string Status()
    {
        bool overlayActive = _activeOverlay() is not null;
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["pid"] = Environment.ProcessId,
            ["tray"] = _trayReady(),
            ["overlay"] = new Dictionary<string, object?>
            {
                ["active"] = overlayActive,
                ["windows"] = AppWindowEnumerator.GetOverlayWindowHwnds(),
            },
            ["pins"] = _pins.Snapshot(),
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>snip.interactive(§8.2):等价托盘 F1;output.file 可选,缺省=clipboard(语义对齐 -o)。立即应答。</summary>
    private string SnipInteractive(JsonElement root)
    {
        var targets = new List<OutputTarget>();
        if (root.TryGetProperty("output", out var output))
        {
            if (output.ValueKind != JsonValueKind.Object
                || !output.TryGetProperty("file", out var fileEl)
                || fileEl.ValueKind != JsonValueKind.String)
            {
                return Err("output 仅支持 {\"file\":\"路径\"}");
            }
            var path = Environment.ExpandEnvironmentVariables(fileEl.GetString()!);
            if (string.IsNullOrWhiteSpace(path))
            {
                return Err("output.file 为空");
            }
            targets.Add(new OutputTarget(OutputTargetKind.File, path));
        }
        if (targets.Count == 0)
        {
            targets.Add(new OutputTarget(OutputTargetKind.Clipboard));
        }

        // 受理即应答:启动在内部异步进行(遮罩线程/冻结画面);同步落定视为忙或启动失败
        var task = _startSnip(targets);
        if (task.Wait(500))
        {
            var (ok, err) = task.Result;
            return ok ? Ok() : Err(err ?? "截屏启动失败");
        }
        return Ok();
    }

    /// <summary>snip.setRegion(§8.2):调 SnipOverlay.PostSetRegion(虚拟桌面物理 px,§8.1)。</summary>
    private string SnipSetRegion(JsonElement root)
    {
        if (!TryGetInt(root, "x", out var x) || !TryGetInt(root, "y", out var y)
            || !TryGetInt(root, "w", out var w) || !TryGetInt(root, "h", out var h))
        {
            return Err("snip.setRegion 需要整数 x/y/w/h");
        }
        if (w <= 0 || h <= 0)
        {
            return Err($"w/h 必须为正:{w}x{h}");
        }
        return WithOverlay(o => { o.PostSetRegion(x, y, w, h); return Ok(); });
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    internal static string Ok() => "{\"ok\":true}";

    internal static string Err(string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = message }, JsonOpts);

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 取消路径 */ }
        }
        _cts?.Dispose();
    }
}
