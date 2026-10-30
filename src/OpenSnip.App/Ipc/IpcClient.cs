using System.IO;
using System.IO.Pipes;

namespace OpenSnip.App.Ipc;

/// <summary>转发结果状态。</summary>
public enum IpcRelayStatus
{
    /// <summary>已连上首实例并完成转发。</summary>
    Connected,
    /// <summary>无监听:本进程判定为首实例。</summary>
    NotAvailable,
    /// <summary>--block:已收到首实例回写。</summary>
    Blocked,
    /// <summary>--block:60s 未收到回写。</summary>
    Timeout,
    /// <summary>转发过程异常。</summary>
    Error,
}

/// <summary>IpcClient.RelayAsync 的结果。</summary>
public sealed record IpcRelayResult(IpcRelayStatus Status, bool Ok, string? Message)
{
    /// <summary>是否已连上首实例(true ⇒ 本进程作为第二实例退出)。</summary>
    public bool Connected => Status != IpcRelayStatus.NotAvailable;

    /// <summary>进程退出码:转发即 0;--block OK=0 / ERR=1;超时=2;错误=1。</summary>
    public int ExitCode => Status switch
    {
        IpcRelayStatus.Connected => 0,
        IpcRelayStatus.Blocked => Ok ? 0 : 1,
        IpcRelayStatus.Timeout => 2,
        IpcRelayStatus.Error => 1,
        _ => 0,
    };
}

public static class IpcClient
{
    /// <summary>
    /// 第二实例路径(SPEC §3.4):把完整命令行发给首实例;
    /// block=false 发完即回;block=true 等首实例执行完成后回写(60s 超时)。
    /// 连不上(无监听/超时)返回 NotAvailable ⇒ 调用方按首实例继续。
    /// </summary>
    public static async Task<IpcRelayResult> RelayAsync(IReadOnlyList<string> args, bool block, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(IpcProtocol.ClientConnectTimeout, ct);
        }
        catch (TimeoutException) { return new IpcRelayResult(IpcRelayStatus.NotAvailable, false, null); }
        catch (IOException) { return new IpcRelayResult(IpcRelayStatus.NotAvailable, false, null); }

        try
        {
            await pipe.WriteAsync(IpcProtocol.EncodeRequest(args), ct);
            await pipe.FlushAsync(ct);
            if (!block)
            {
                return new IpcRelayResult(IpcRelayStatus.Connected, true, null); // SPEC:发完即退
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(IpcProtocol.ClientBlockTimeout);
            var (ok, msg) = await IpcProtocol.ReadResponseAsync(pipe, timeoutCts.Token);
            return new IpcRelayResult(IpcRelayStatus.Blocked, ok, msg);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IpcRelayResult(IpcRelayStatus.Timeout, false, "等待首实例回写超时(60s)");
        }
        catch (Exception ex)
        {
            return new IpcRelayResult(IpcRelayStatus.Error, false, ex.Message);
        }
    }
}
