using System.IO;
using System.IO.Pipes;
using OpenSnip.Core;

namespace OpenSnip.App.Ipc;

/// <summary>
/// 单实例命名管道服务端(管道名 SPEC §3.4 固定:OpenSnip.Ipc)。
/// 循环:等连接 → 读完整命令行 → 执行命令(可能耗时;--block 语义要求执行完成后才回写)→
/// 写一行结果 → 断开。上一会话处理期间即预先创建下一管道实例,消除"断开与再次监听之间"的重连竞态。
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<string>, Task<(bool Ok, string? Error)>> _handler;
    private readonly SemaphoreSlim _sessionGate = new(1, 1); // 同一时刻只执行一个命令
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public IpcServer(Func<IReadOnlyList<string>, Task<(bool Ok, string? Error)>> handler)
        => _handler = handler;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private static NamedPipeServerStream CreatePipe() =>
        new(IpcProtocol.PipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

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
                    PerfLog.Log("App", $"ipc:wait-connection-failed {ex.Message}");
                    continue;
                }

                var next = CreatePipe(); // 提前备好下一实例
                bool cancelled = false;
                try
                {
                    await HandleSessionAsync(pipe, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    PerfLog.Log("App", $"ipc:session-error {ex.GetType().Name}: {ex.Message}");
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

    private async Task HandleSessionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            List<string> args;
            try
            {
                args = await IpcProtocol.ReadRequestAsync(pipe, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                PerfLog.Log("App", $"ipc:bad-request {ex.Message}");
                return;
            }
            PerfLog.Log("App", $"ipc:received args=[{string.Join(' ', args)}]");

            (bool Ok, string? Error) resp;
            try
            {
                resp = await _handler(args);
            }
            catch (Exception ex)
            {
                resp = (false, ex.Message);
            }

            try
            {
                await pipe.WriteAsync(IpcProtocol.EncodeResponse(resp.Ok, resp.Error), ct);
                await pipe.FlushAsync(ct);
                pipe.WaitForPipeDrain();
            }
            catch (IOException)
            {
                // 非阻塞客户端已断开,属预期(命令仍会执行)
            }
            catch (OperationCanceledException) { /* 退出中 */ }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 取消路径 */ }
        }
        _cts?.Dispose();
        _sessionGate.Dispose();
    }
}
