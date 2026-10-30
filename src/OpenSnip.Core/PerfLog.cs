using System.Diagnostics;
using System.Threading.Channels;

namespace OpenSnip.Core;

/// <summary>
/// 性能日志:异步追加到 logs\perf.log,永不阻塞业务线程。
/// 日志目录解析:从程序目录向上找 global.json(仓库根,最多 6 层),找不到则退回 程序目录\logs。
/// </summary>
public static class PerfLog
{
    private static readonly System.Threading.Channels.Channel<string> LogChannel =
        System.Threading.Channels.Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private static readonly string LogPath = ResolveLogPath();

    static PerfLog()
    {
        // 后台消费者:逐行追加(每次写完即落盘,进程崩溃最多丢行不丢序)
        _ = Task.Run(async () =>
        {
            await foreach (var line in LogChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    // FileShare.ReadWrite:允许 App/测试等多进程同时追加(短行追加近原子)
                    await using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    await using var writer = new StreamWriter(fs);
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                catch
                {
                    // 日志失败静默,绝不影响业务
                }
            }
        });
    }

    /// <summary>当前 perf.log 完整路径(测试/诊断用)。</summary>
    public static string CurrentLogPath => LogPath;

    /// <summary>异步追加一行:时间戳 + [类别] + [T线程] + 消息。</summary>
    public static void Log(string category, string message)
    {
        LogChannel.Writer.TryWrite(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t[{category}]\t[T{Environment.CurrentManagedThreadId}]\t{message}");
    }

    /// <summary>计时执行并打点,返回耗时。</summary>
    public static TimeSpan Measure(string category, string name, Action action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            action();
        }
        catch
        {
            sw.Stop();
            Log(category, $"{name} 异常 {sw.Elapsed.TotalMilliseconds:F2} ms");
            throw;
        }
        sw.Stop();
        Log(category, $"{name} {sw.Elapsed.TotalMilliseconds:F2} ms");
        return sw.Elapsed;
    }

    /// <summary>计时执行并打点,返回(耗时, 结果)——结果由调用方取。</summary>
    public static TimeSpan Measure<T>(string category, string name, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        var value = action()!;
        sw.Stop();
        Log(category, $"{name} {sw.Elapsed.TotalMilliseconds:F2} ms");
        return sw.Elapsed;
    }

    private static string ResolveLogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return Path.Combine(dir.FullName, "logs", "perf.log");
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "logs", "perf.log");
    }
}
