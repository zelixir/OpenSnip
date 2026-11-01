using System.Diagnostics;
using OpenSnip.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenSnip.Core.Tests;

/// <summary>
/// 热角监视器行为级测试:角区判定纯函数 + 驻留状态机(经公开事件收口)+
/// 钩子安装/卸载生命周期。不移动用户真实光标。
/// </summary>
public class HotCornerMonitorTests
{
    private readonly ITestOutputHelper _output;

    public HotCornerMonitorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static PxRect PrimaryBounds()
        => DisplayManager.Instance.Displays[0].Bounds;

    [Fact]
    public void CornerIndexOf_Math()
    {
        var b = new PxRect(0, 0, 1920, 1080);
        int zone = 6;
        Assert.Equal(0, HotCornerMonitor.CornerIndexOf(new PxPoint(0, 0), b, zone));       // 左上
        Assert.Equal(1, HotCornerMonitor.CornerIndexOf(new PxPoint(1919, 0), b, zone));    // 右上
        Assert.Equal(2, HotCornerMonitor.CornerIndexOf(new PxPoint(0, 1079), b, zone));    // 左下
        Assert.Equal(3, HotCornerMonitor.CornerIndexOf(new PxPoint(1919, 1079), b, zone)); // 右下
        Assert.Equal(-1, HotCornerMonitor.CornerIndexOf(new PxPoint(960, 540), b, zone));  // 屏中心
        Assert.Equal(-1, HotCornerMonitor.CornerIndexOf(new PxPoint(10, 10), b, zone));    // 角区外
        Assert.Equal(-1, HotCornerMonitor.CornerIndexOf(new PxPoint(-5, -5), b, zone));    // 屏外
        // 负坐标虚拟桌面(副屏在左)
        var b2 = new PxRect(-1920, 0, 1920, 1080);
        Assert.Equal(1, HotCornerMonitor.CornerIndexOf(new PxPoint(-1, 0), b2, zone));     // 该屏右上
        Assert.Equal(0, HotCornerMonitor.CornerIndexOf(new PxPoint(-1920, 0), b2, zone));  // 该屏左上
    }

    [Fact]
    public void Dwell_StateMachine_FiresOnce_AndRearms()
    {
        var bounds = PrimaryBounds();
        var cornerPt = new PxPoint(bounds.X + 1, bounds.Y + 1); // 左上角
        var awayPt = new PxPoint(bounds.X + 400, bounds.Y + 400);
        int firedCorner = -100;
        int fireCount = 0;

        using var monitor = new HotCornerMonitor(TimeSpan.FromMilliseconds(100));
        monitor.CornerEntered += c => { fireCount++; firedCorner = c; };
        monitor.Enabled = true;
        Thread.Sleep(200); // 等钩子安装 + 定时器起步
        monitor.SetHookFeedEnabled(false); // 暂停物理鼠标喂数:状态机由测试确定性驱动

        long t0 = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        monitor.FeedMove(cornerPt, t0);            // 进角
        monitor.Evaluate(t0 + 50);                 // 驻留未满 → 不触发
        monitor.FeedMove(cornerPt, t0 + 60);       // 角内微动(刷新驻留)
        monitor.Evaluate(t0 + 80);                 // 仍未满
        var fire1 = monitor.Evaluate(t0 + 300);    // 距最后 move 240ms ≥ 100ms → 触发
        if (fire1 is not null)
        {
            monitor.DebugPostFire(fire1.Value);    // 生产路径等价:定时器→PostMessage→钩子线程事件
        }

        // 事件经钩子线程异步送达,等待最多 2s
        var sw = Stopwatch.StartNew();
        while (fireCount == 0 && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(30);
        }
        Assert.Equal(1, fireCount);
        Assert.Equal(0, firedCorner); // 左上

        // 已发射不重复:继续 Tick 不再触发
        monitor.Evaluate(t0 + 1000);
        Thread.Sleep(200);
        Assert.Equal(1, fireCount);

        // 离开角区 → 重新武装 → 再次驻留再次触发
        monitor.FeedMove(awayPt, t0 + 1100);
        monitor.Evaluate(t0 + 1200);
        monitor.FeedMove(cornerPt, t0 + 1300);
        var fire2 = monitor.Evaluate(t0 + 1450);
        if (fire2 is not null)
        {
            monitor.DebugPostFire(fire2.Value);
        }
        sw.Restart();
        while (fireCount < 2 && sw.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(30);
        }
        Assert.Equal(2, fireCount);
        Assert.Equal(0, firedCorner);

        monitor.SetHookFeedEnabled(true);
        monitor.Enabled = false;
    }

    [Fact]
    public void Disabled_NoEvents()
    {
        var bounds = PrimaryBounds();
        var cornerPt = new PxPoint(bounds.X + 1, bounds.Y + 1);
        int fireCount = 0;
        using var monitor = new HotCornerMonitor(TimeSpan.FromMilliseconds(50));
        monitor.CornerEntered += _ => fireCount++;
        // 不启用 Enabled:即使鼠标一直在角上也不触发
        monitor.FeedMove(cornerPt, 0);
        monitor.Evaluate(10_000);
        Thread.Sleep(300);
        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void HookLifecycle_EnableDisable_Dispose()
    {
        using var monitor = new HotCornerMonitor(TimeSpan.FromMilliseconds(60));
        monitor.Enabled = true;
        Thread.Sleep(150);
        Assert.True(monitor.IsHookInstalled, "启用后应已安装 WH_MOUSE_LL 钩子");
        monitor.Enabled = false;
        Thread.Sleep(150);
        Assert.False(monitor.IsHookInstalled, "停用后应卸载钩子");

        monitor.Enabled = true;
        Thread.Sleep(150);
        Assert.True(monitor.IsHookInstalled);
        monitor.Dispose(); // Dispose 内部卸钩子 + 停线程
        Assert.False(monitor.IsHookInstalled);
        _output.WriteLine("钩子生命周期 OK");
    }
}
