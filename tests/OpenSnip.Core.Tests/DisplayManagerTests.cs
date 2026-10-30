using OpenSnip.Core;
using Xunit;

namespace OpenSnip.Core.Tests;

/// <summary>显示器枚举冒烟(§3.1:Displays 非空)。</summary>
public class DisplayManagerTests
{
    [Fact]
    public void Displays_NonEmpty()
    {
        var dm = new DisplayManager();
        Assert.NotEmpty(dm.Displays);
    }

    [Fact]
    public void Displays_ExactlyOnePrimary_WithSaneData()
    {
        var dm = new DisplayManager();
        var primaries = dm.Displays.Where(d => d.IsPrimary).ToList();
        Assert.Single(primaries);

        foreach (var d in dm.Displays)
        {
            Assert.False(d.Bounds.IsEmpty);
            Assert.StartsWith(@"\\.\DISPLAY", d.DeviceName);
            Assert.True(d.DpiScale >= 1.0f, $"DpiScale={d.DpiScale} 应 ≥ 1.0");
            Assert.False(d.Bounds.IsEmpty);
            Assert.True(d.Bounds.Width > 0 && d.Bounds.Height > 0);
            // 工作区不超出显示器边界
            Assert.True(d.WorkArea.X >= d.Bounds.X && d.WorkArea.Y >= d.Bounds.Y);
        }
    }

    [Fact]
    public void Instance_IsLazySingleton()
    {
        Assert.Same(DisplayManager.Instance, DisplayManager.Instance);
        Assert.NotEmpty(DisplayManager.Instance.Displays);
    }

    [Fact]
    public void GetDisplayByIndex_OutOfRange_ReturnsNull()
    {
        var dm = new DisplayManager();
        Assert.Null(dm.GetDisplayByIndex(-1));
        Assert.Null(dm.GetDisplayByIndex(dm.Displays.Count));
        Assert.NotNull(dm.GetDisplayByIndex(0));
    }

    [Fact]
    public void GetDisplayAt_PrimaryTopLeft_ReturnsSomeDisplay()
    {
        var dm = new DisplayManager();
        var primary = dm.Displays.First(d => d.IsPrimary);
        var hit = dm.GetDisplayAt(new PxPoint(primary.Bounds.X + 1, primary.Bounds.Y + 1));
        Assert.NotNull(hit);
    }
}
