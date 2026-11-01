using OpenSnip.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenSnip.Core.Tests;

/// <summary>
/// 超级截屏触发器行为级测试(契约 §16 验收:不碰真实输入)。
/// 经 internal Feed* 缝确定性驱动:吞 Win / Win+左键触发 / 抬起复位 / 关闭放行 / 事件经钩子线程。
/// </summary>
public class SuperSnipTriggerTests
{
    private readonly ITestOutputHelper _output;

    public SuperSnipTriggerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Disabled_AllKeysPassThrough()
    {
        using var trigger = new SuperSnipTrigger();
        int fired = 0;
        trigger.SnipRequested += () => fired++;

        Assert.False(trigger.FeedKey(0x5B, down: true));  // LWIN down 放行
        Assert.False(trigger.WinKeyDown);                  // 不进入按住状态
        Assert.False(trigger.FeedKey(0x5B, down: false)); // up 放行
        Assert.False(trigger.FeedLButtonDown());           // 左键放行
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Enabled_WinIsSwallowed_AndStateTracked()
    {
        using var trigger = new SuperSnipTrigger();
        trigger.Enabled = true;
        Thread.Sleep(150); // 等钩子安装

        Assert.True(trigger.IsHookInstalled);
        Assert.False(trigger.WinKeyDown);

        Assert.True(trigger.FeedKey(0x5B, down: true));   // LWIN down → 吞
        Assert.True(trigger.WinKeyDown);                   // 进入按住状态
        Assert.True(trigger.FeedKey(0x5C, down: true));   // RWIN down → 吞
        Assert.True(trigger.FeedKey(0x5B, down: false));  // up → 吞
        Assert.False(trigger.WinKeyDown);                  // 抬起复位
        Assert.True(trigger.FeedKey(0x5C, down: false));  // RWIN up → 吞

        Assert.False(trigger.FeedKey(0x41, down: true));  // 普通 A 键 → 放行(不吞)
        trigger.Enabled = false;
        Thread.Sleep(150);
        Assert.False(trigger.IsHookInstalled);
    }

    [Fact]
    public void WinHeld_LButtonDown_TriggersSnipRequested()
    {
        using var trigger = new SuperSnipTrigger();
        var fired = new ManualResetEventSlim(false);
        trigger.SnipRequested += () => fired.Set();
        trigger.Enabled = true;
        Thread.Sleep(150);

        Assert.True(trigger.FeedKey(0x5B, down: true));  // Win 按住
        Assert.True(trigger.FeedLButtonDown());           // 左键按下 → 吞 + 触发(调用线程=钩子线程)
        Assert.True(fired.Wait(2000), "SnipRequested 未触发");

        // 触发后按住状态已消费:抬起不再吞、再次左键(未按 Win)不触发
        int second = 0;
        trigger.SnipRequested += () => second++;
        Assert.False(trigger.FeedLButtonDown());
        Assert.Equal(0, second);
        trigger.Enabled = false;
    }

    [Fact]
    public void WinHeld_Rwin_AlsoTriggers()
    {
        using var trigger = new SuperSnipTrigger();
        var fired = new ManualResetEventSlim(false);
        trigger.SnipRequested += () => fired.Set();
        trigger.Enabled = true;
        Thread.Sleep(150);

        Assert.True(trigger.FeedKey(0x5C, down: true));  // RWIN
        Assert.True(trigger.FeedLButtonDown());
        Assert.True(fired.Wait(2000));
        trigger.Enabled = false;
    }

    [Fact]
    public void Enabled_MidHold_Reset_PreventsStuckState()
    {
        using var trigger = new SuperSnipTrigger();
        trigger.Enabled = true;
        Thread.Sleep(150);
        Assert.True(trigger.FeedKey(0x5B, down: true));
        // 期间禁用再启用:按住状态必须复位(否则"粘滞 Win")
        trigger.Enabled = false;
        trigger.Enabled = true;
        Thread.Sleep(150);
        Assert.False(trigger.WinKeyDown);
        // 复位后:未按 Win 的左键不触发
        Assert.False(trigger.FeedLButtonDown());
        trigger.Enabled = false;
    }
}
