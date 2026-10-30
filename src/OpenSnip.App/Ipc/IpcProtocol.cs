using System.IO;
using System.IO.Pipes;
using System.Text;

namespace OpenSnip.App.Ipc;

/// <summary>
/// 单实例 IPC 线协议(SPEC §3.4,管道名固定 "OpenSnip.Ipc"):
///   请求:多行 UTF-8 文本 —— "REQ" / 参数行数 N / N 行参数(每行一个 argv);
///   响应:单行 UTF-8 —— "OK" 或 "ERR &lt;消息&gt;"(首实例执行完成后回写,--block 语义)。
/// 分行符 '\n';按定长行数读取,不依赖 EOF(非阻塞客户端发完即断)。
/// </summary>
public static class IpcProtocol
{
    /// <summary>SPEC §3.4 固定管道名。</summary>
    public const string PipeName = "OpenSnip.Ipc";

    /// <summary>客户端连接尝试窗口(窗口内仍无监听 ⇒ 判定自己是首实例)。</summary>
    public static readonly TimeSpan ClientConnectTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>--block 等待回写超时(SPEC:60s)。</summary>
    public static readonly TimeSpan ClientBlockTimeout = TimeSpan.FromSeconds(60);

    public static byte[] EncodeRequest(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append("REQ\n").Append(args.Count).Append('\n');
        foreach (var a in args)
        {
            sb.Append(a.Replace('\n', ' ')).Append('\n'); // 参数含换行属契约外场景,降级为空格
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] EncodeResponse(bool ok, string? message)
    {
        var line = ok ? "OK" : "ERR " + (message ?? "未知错误");
        line = line.Replace('\n', ' ').Replace('\r', ' ');
        return Encoding.UTF8.GetBytes(line + "\n");
    }

    /// <summary>读完整请求:先读头两行,再读定长 N 行(不依赖 EOF)。</summary>
    public static async Task<List<string>> ReadRequestAsync(PipeStream pipe, CancellationToken ct)
    {
        var header = await ReadLineAsync(pipe, ct);
        if (header != "REQ")
        {
            throw new InvalidDataException("IPC 请求头错误:" + header);
        }
        var countLine = await ReadLineAsync(pipe, ct);
        if (!int.TryParse(countLine, out var count) || count < 0 || count > 256)
        {
            throw new InvalidDataException("IPC 参数行数非法:" + countLine);
        }
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(await ReadLineAsync(pipe, ct));
        }
        return list;
    }

    /// <summary>读一行响应:OK / ERR 消息。</summary>
    public static async Task<(bool Ok, string? Message)> ReadResponseAsync(PipeStream pipe, CancellationToken ct)
    {
        var line = await ReadLineAsync(pipe, ct);
        if (line == "OK")
        {
            return (true, null);
        }
        if (line.StartsWith("ERR", StringComparison.Ordinal))
        {
            return (false, line.Length > 4 ? line[4..] : "未知错误");
        }
        return (false, line.Length == 0 ? "首实例异常断开" : line);
    }

    private static async Task<string> ReadLineAsync(PipeStream pipe, CancellationToken ct)
    {
        var buf = new MemoryStream(); // 行内容很短(命令行参数),托管缓冲可接受(非位图数据,不触红线 §0.4)
        var one = new byte[1];
        while (true)
        {
            int n = await pipe.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0)
            {
                break; // 对端断开
            }
            if (one[0] == (byte)'\n')
            {
                break;
            }
            buf.WriteByte(one[0]);
            if (buf.Length > 64 * 1024)
            {
                throw new InvalidDataException("IPC 行过长");
            }
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }
}
