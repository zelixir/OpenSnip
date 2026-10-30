using OpenSnip.Core;
using Xunit;

namespace OpenSnip.Core.Tests;

/// <summary>窗口枚举与命中测试冒烟(§3.1:非空且含自身进程外窗口;不弹 UI)。</summary>
public class WindowTests
{
    [Fact]
    public void GetHitTestableTopLevel_NonEmpty_ContainsExternalProcessWindows()
    {
        var windows = WindowEnumerator.GetHitTestableTopLevel();
        Assert.NotEmpty(windows);

        string ownExe = Environment.ProcessPath is { } p ? Path.GetFileName(p) : string.Empty;
        // 桌面会话里必然存在 explorer 等外部进程窗口
        Assert.Contains(windows, w => !string.Equals(w.ExeName, ownExe, StringComparison.OrdinalIgnoreCase));

        foreach (var w in windows)
        {
            Assert.NotEqual(IntPtr.Zero, w.Handle);
            Assert.False(w.Bounds.IsEmpty);
        }
    }

    [Fact]
    public void HitTopToBottom_WindowCenter_ContainsThatWindow()
    {
        var windows = WindowEnumerator.GetHitTestableTopLevel();
        // 选一个足够大的窗口,取"它自己 Bounds"的中心点,
        // 保证点与窗口处于同一坐标系(规避宿主进程 DPI 感知差异)
        var target = windows.FirstOrDefault(w => w.Bounds.Width > 200 && w.Bounds.Height > 200);
        if (target is null)
        {
            return; // 环境异常时跳过(冒烟级别,不做脆弱断言)
        }
        var center = new PxPoint(
            target.Bounds.X + target.Bounds.Width / 2,
            target.Bounds.Y + target.Bounds.Height / 2);

        var hitTester = new WindowHitTester();
        var hits = hitTester.HitTopToBottom(center);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, w => w.Handle == target.Handle);
        // Z 序:第一个即最顶
        Assert.All(hits, w => Assert.False(w.Bounds.IsEmpty));
    }
}
