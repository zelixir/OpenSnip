using System.Diagnostics;
using OpenSnip.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenSnip.Core.Tests;

/// <summary>
/// 元素检测确定性测试:自建 Win32 宿主窗口(2 按钮 + 1 静态文本),
/// UIA 树内容完全可控 —— 不依赖记事本/外部应用,零弹窗风暴。
/// 窗口在专用 STA 线程上创建并由其泵消息(UIA 的 WM_GETOBJECT 依赖目标线程消息泵),
/// 小尺寸、短生命周期、位于主屏右下角。
/// </summary>
public class ElementDetectorHostWindowTests : IClassFixture<HostWindowFixture>
{
    private readonly HostWindowFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ElementDetectorHostWindowTests(HostWindowFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void Detect_ContainsExpectedControls_WithNames()
    {
        if (_fixture.Unavailable)
        {
            _output.WriteLine("宿主窗口创建失败,跳过。");
            return;
        }
        using var detector = new ElementDetector();
        var elements = detector.Detect(_fixture.Hwnd);

        _output.WriteLine($"Detect 返回 {elements.Count} 个元素");
        Assert.NotEmpty(elements);

        // 内容断言:两个按钮(名字精确匹配)
        var buttons = elements.Where(e => e.ControlType == "Button").ToList();
        Assert.True(buttons.Count >= 2, $"应至少检出 2 个 Button,实际 {buttons.Count}");
        Assert.Contains(buttons, b => b.Name == HostWindowFixture.ButtonAName);
        Assert.Contains(buttons, b => b.Name == HostWindowFixture.ButtonBName);

        // 文本元素
        Assert.Contains(elements, e => e.ControlType == "Text" && e.Name == HostWindowFixture.LabelName);

        // 根:depth 0,类名匹配
        var root = elements.FirstOrDefault(e => e.Depth == 0);
        Assert.NotNull(root);
        Assert.Equal(HostWindowFixture.ClassName, root.ClassName);

        // 深度上限与物理像素合法性
        Assert.All(elements, e => Assert.InRange(e.Depth, 0, 12));
        Assert.All(elements, e => Assert.True(e.Bounds.Width >= 0 && e.Bounds.Height >= 0));
    }

    [Fact]
    public void Detect_Cached_SecondCallFaster()
    {
        if (_fixture.Unavailable)
        {
            return;
        }
        using var detector = new ElementDetector();
        var first = detector.Detect(_fixture.Hwnd);
        Assert.NotEmpty(first);
        detector.Invalidate(_fixture.Hwnd);
        var second = detector.Detect(_fixture.Hwnd);
        Assert.NotEmpty(second);

        var sw = Stopwatch.StartNew();
        var cached = detector.Detect(_fixture.Hwnd);
        sw.Stop();
        Assert.Equal(second.Count, cached.Count);
        Assert.True(sw.ElapsedMilliseconds < 50, $"缓存命中应 <50ms,实际 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void HitTest_OnButtonA_ReturnsButtonAsLeaf()
    {
        if (_fixture.Unavailable || !_fixture.TryGetScreenRect(_fixture.ButtonA, out var buttonRect))
        {
            _output.WriteLine("宿主窗口不可用,跳过。");
            return;
        }
        using var detector = new ElementDetector();
        var pt = new PxPoint(
            buttonRect.X + buttonRect.Width / 2,
            buttonRect.Y + buttonRect.Height / 2);
        var chain = detector.HitTest(pt);

        Assert.NotEmpty(chain);
        Assert.Equal(HostWindowFixture.ButtonAName, chain[0].Name);          // 叶 = 按钮A
        Assert.True(chain[0].Bounds.Contains(pt.X, pt.Y), "叶应包含命中点"); // 叶包含命中点
        Assert.True(chain[0].Depth >= chain[^1].Depth, "链应 叶→根 排列");   // 叶→根
        Assert.Equal(0, chain[^1].Depth);                                    // 根 = 顶层窗口
        Assert.Equal(_fixture.Hwnd, chain[^1].WindowHandle);                 // 根 = 宿主窗口
    }

    [Fact]
    public void HitTest_InvalidateCycle_StillWorks()
    {
        if (_fixture.Unavailable)
        {
            return;
        }
        using var detector = new ElementDetector();
        for (int i = 0; i < 3; i++)
        {
            detector.Invalidate(_fixture.Hwnd);
            var chain = detector.HitTest(_fixture.ButtonCenter);
            Assert.NotEmpty(chain);
            Assert.Equal(HostWindowFixture.ButtonAName, chain[0].Name);
        }
    }
}

/// <summary>自建宿主窗口夹具:主窗口 + 2 按钮 + 1 静态文本(专用 STA 线程 + 消息泵)。</summary>
public sealed class HostWindowFixture : IDisposable
{
    public const string ClassName = "OpenSnip.TestHostWnd";
    public const string ButtonAName = "测试按钮A";
    public const string ButtonBName = "测试按钮B";
    public const string LabelName = "测试文本";

    private static NativeMethods.WndProc? _sharedWndProc; // 防回收
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Thread _thread;
    private bool _disposed;

    public nint Hwnd { get; private set; }
    public nint ButtonA { get; private set; }
    public nint ButtonB { get; private set; }
    public nint Label { get; private set; }
    public bool Unavailable => Hwnd == IntPtr.Zero;

    public HostWindowFixture()
    {
        _thread = new Thread(ThreadProc)
        {
            Name = "OpenSnip.TestHostWnd",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(5000))
        {
            throw new InvalidOperationException("测试宿主窗口线程 5s 未就绪。");
        }
        Thread.Sleep(300); // UIA 树稳定
    }

    private void ThreadProc()
    {
        var wndProc = new NativeMethods.WndProc(DefProc);
        _sharedWndProc = wndProc; // 防回收
        var wc = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = ClassName,
        };
        _ = NativeMethods.RegisterClassExW(ref wc); // 重复注册返回 0,忽略

        var pb = DisplayManager.Instance.Displays[0].Bounds;
        int x = pb.X + Math.Max(0, pb.Width - 260);
        int y = pb.Y + Math.Max(0, pb.Height - 180);

        Hwnd = NativeMethods.CreateWindowExW(
            0x00000008 /* WS_EX_TOPMOST:保证 ElementFromPoint 命中本窗口,不受用户桌面遮挡 */,
            ClassName, "OpenSnip 元素检测测试宿主",
            0x00CF0000 /* WS_OVERLAPPEDWINDOW */ | 0x10000000 /* WS_VISIBLE */,
            x, y, 230, 130, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
        {
            _ready.Set();
            return;
        }

        // 子控件:WS_CHILD | WS_VISIBLE
        const uint childStyle = 0x50000000;
        ButtonA = NativeMethods.CreateWindowExW(0, "BUTTON", ButtonAName, childStyle,
            12, 12, 95, 30, Hwnd, new IntPtr(1), wc.hInstance, IntPtr.Zero);
        ButtonB = NativeMethods.CreateWindowExW(0, "BUTTON", ButtonBName, childStyle,
            115, 12, 95, 30, Hwnd, new IntPtr(2), wc.hInstance, IntPtr.Zero);
        Label = NativeMethods.CreateWindowExW(0, "STATIC", LabelName, childStyle,
            12, 52, 198, 30, Hwnd, new IntPtr(3), wc.hInstance, IntPtr.Zero);

        _ready.Set();

        // 消息泵:UIA 跨线程 SendMessage(WM_GETOBJECT)依赖本线程泵消息
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
        NativeMethods.DestroyWindow(Hwnd);
    }

    /// <summary>按钮 A 的屏幕矩形(物理像素)。</summary>
    public bool TryGetScreenRect(nint hwnd, out PxRect rect)
        => NativeMethodsProxy.GetWindowRect(hwnd, out rect);

    /// <summary>按钮 A 中心点。</summary>
    public PxPoint ButtonCenter
    {
        get
        {
            NativeMethodsProxy.GetWindowRect(ButtonA, out var r);
            return new PxPoint(r.X + r.Width / 2, r.Y + r.Height / 2);
        }
    }

    private static nint DefProc(nint hwnd, int msg, nint wParam, nint lParam)
        => NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        if (Hwnd != IntPtr.Zero)
        {
            // WM_CLOSE → DefWindowProc 销毁窗口 → GetMessage 返回 0 → 线程收尾(子控件随父销毁)
            NativeMethods.PostMessageW(Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }
        _sharedWndProc = null;
        _ready.Dispose();
    }
}
