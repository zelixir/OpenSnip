using OpenSnip.Core;
using Xunit;

namespace OpenSnip.Core.Tests;

/// <summary>热键生命周期 + PerfLog 冒烟。不合成输入、不弹 UI。</summary>
public class HotkeyAndPerfLogTests
{
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;
    private const uint VkF13 = 0x6C;
    private const uint VkF14 = 0x6D;
    private const uint VkF15 = 0x6E;

    [Fact]
    public void Register_Unregister_Dispose_Lifecycle()
    {
        using var manager = new GlobalHotkeyManager();
        int fired = 0;
        manager.HotkeyPressed += _ => fired++;

        // 尝试一组冷门组合,至少一个应注册成功(避免环境冲突导致脆弱断言)
        bool registered =
            manager.Register(1, ModControl | ModAlt | ModShift, VkF13) ||
            manager.Register(1, ModWin | ModShift, VkF14) ||
            manager.Register(1, ModControl | ModAlt, VkF15);
        Assert.True(registered, "三个冷门热键组合全部注册失败,环境异常。");

        manager.Unregister(1); // 幂等
        manager.Unregister(1);
        manager.Dispose();
        Assert.Equal(0, fired); // 未合成输入,不应触发
    }

    [Fact]
    public void PerfLog_Measure_ReturnsElapsed_And_WritesFile()
    {
        int value = 0;
        var elapsed = PerfLog.Measure("Core.Tests", "Measure冒烟", () => { value = 42; });
        Assert.True(elapsed >= TimeSpan.Zero);
        Assert.Equal(42, value);

        PerfLog.Log("Core.Tests", "Log冒烟行");

        // 异步落盘:轮询等待最多 3s
        string path = PerfLog.CurrentLogPath;
        for (int i = 0; i < 30; i++)
        {
            if (File.Exists(path))
            {
                try
                {
                    // 共享读:写进程可能正持有追加句柄
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    var text = reader.ReadToEnd();
                    if (text.Contains("Log冒烟行"))
                    {
                        Assert.Contains("[Core.Tests]", text);
                        return;
                    }
                }
                catch (IOException)
                {
                    // 句柄竞争,下一轮重试
                }
            }
            Thread.Sleep(100);
        }
        Assert.Fail($"3s 内 perf.log 未出现冒烟行:path={path}");
    }
}
