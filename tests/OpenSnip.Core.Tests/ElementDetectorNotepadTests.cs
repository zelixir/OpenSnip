using System.Diagnostics;
using OpenSnip.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenSnip.Core.Tests;

/// <summary>
/// 类级夹具:整个测试类只启动/关闭一次记事本(避免反复弹窗打扰用户)。
/// 清理策略:WM_CLOSE 优雅关闭优先;强杀会触发 Win11 记事本自动重启,产生"幽灵残留"。
/// </summary>
public sealed class NotepadFixture : IDisposable
{
    private readonly object _gate = new();
    private Process? _notepad;
    private bool _disposed;

    public nint Hwnd { get; }
    public uint WindowPid { get; }

    /// <summary>false=复用用户已开的记事本(只读,不负责关闭);true=本夹具启动、负责关闭。</summary>
    public bool Owned { get; }

    public NotepadFixture()
    {
        // 先看是否已有记事本窗口(单实例+标签页模式:已有实例时再启动只会开新标签)
        var existing = NativeMethodsProxy.FindWindowByClass("Notepad");
        if (existing != IntPtr.Zero)
        {
            Hwnd = existing;
            _ = NativeMethodsProxy.GetWindowThreadProcessId(Hwnd, out uint pid);
            WindowPid = pid;
            Owned = false; // 只读复用用户的窗口,Dispose 不关闭
            return;
        }

        Process? started = null;
        for (int attempt = 1; attempt <= 3 && started is null; attempt++)
        {
            try
            {
                started = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            }
            catch
            {
                // 商店版刚被关闭后短暂不可激活:重试
                Thread.Sleep(600);
            }
        }
        _notepad = started;
        if (started is null)
        {
            return; // 环境无记事本:测试类守卫跳过
        }
        var sw = Stopwatch.StartNew();
        nint? candidate = null;
        var candidateSince = DateTime.UtcNow;
        while (sw.ElapsedMilliseconds < 8000)
        {
            // 用 FindWindowW 取窗口(商店版启动链的启动器进程 MainWindowHandle 不可靠);
            // 候选窗口需稳定存活 ≥500ms 才接受——激活期的瞬态窗口会被销毁,导致 IsWindow=false
            var hwnd = NativeMethodsProxy.FindWindowByClass("Notepad");
            if (hwnd != IntPtr.Zero && NativeMethodsProxy.IsWindow(hwnd))
            {
                if (candidate == hwnd)
                {
                    if ((DateTime.UtcNow - candidateSince).TotalMilliseconds >= 2000)
                    {
                        Hwnd = hwnd;
                        _ = NativeMethodsProxy.GetWindowThreadProcessId(Hwnd, out uint pid);
                        WindowPid = pid;
                        Owned = true;
                        return;
                    }
                }
                else
                {
                    candidate = hwnd;
                    candidateSince = DateTime.UtcNow;
                }
            }
            else
            {
                candidate = null; // 瞬态窗口已销毁:重置候选
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>环境无记事本时为 true(测试跳过)。</summary>
    public bool Unavailable => Hwnd == IntPtr.Zero;

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
        try
        {
            if (!Owned)
            {
                _notepad?.Dispose();
                return; // 复用用户的记事本:只读测试,绝不替用户关窗
            }
            // 优雅关闭优先(WM_CLOSE):强杀会触发 Win11 记事本的自动重启,产生"幽灵残留"
            if (Hwnd != IntPtr.Zero && NativeMethodsProxy.IsWindow(Hwnd))
            {
                NativeMethodsProxy.PostWmClose(Hwnd);
            }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                var alive = false;
                try
                {
                    if (WindowPid != 0)
                    {
                        using var owner = Process.GetProcessById((int)WindowPid);
                        alive = !owner.HasExited;
                    }
                    else if (_notepad is { } np)
                    {
                        np.Refresh();
                        alive = !np.HasExited;
                    }
                }
                catch
                {
                    alive = false; // 进程已不存在
                }
                if (!alive)
                {
                    break;
                }
                Thread.Sleep(150);
            }
            // 仍存活才强杀(接受可能的自动重启副作用)
            if (WindowPid != 0)
            {
                try
                {
                    using var owner = Process.GetProcessById((int)WindowPid);
                    if (!owner.HasExited)
                    {
                        using var tk = Process.Start(new ProcessStartInfo("taskkill", $"/PID {WindowPid} /T /F")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        });
                        tk?.WaitForExit(3000);
                    }
                }
                catch
                {
                    // 进程已不存在:正常
                }
            }
            if (_notepad is { } p && !p.HasExited)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 启动器已退出:正常(商店版常见)
                }
            }
            _notepad?.Dispose();
        }
        catch
        {
            // cleanup 尽力而为
        }
    }
}

/// <summary>
/// 元素检测冒烟(契约 §10 验收:记事本 launch→detect→断言→cleanup)。
/// 内容级断言:树非空/深度上限/存在编辑区元素/命中链 叶→根 语义,而非仅"调用不抛异常"。
/// 整类共享一个记事本实例(类级夹具),不反复弹窗。
/// </summary>
public class ElementDetectorNotepadTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ElementDetectorNotepadTests(NotepadFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public void Notepad_Detect_HasRealTree_WithinCaps()
    {
        if (_fixture.Unavailable)
        {
            _output.WriteLine("环境无记事本,跳过。");
            return;
        }
        var hwnd = _fixture.Hwnd;
        using var detector = new ElementDetector();
        var sw = Stopwatch.StartNew();
        // 契约语义:单次调用最多阻塞 100ms,超时先返回空(结果后台完成后进缓存)。
        // 商店版记事本树大,首次遍历可能超时 → 重试直到命中缓存/完成。
        IReadOnlyList<ElementInfo> elements = Array.Empty<ElementInfo>();
        while (sw.ElapsedMilliseconds < 5000)
        {
            elements = detector.Detect(hwnd);
            if (elements.Count > 0)
            {
                break;
            }
            Thread.Sleep(150);
        }
        sw.Stop();

        _output.WriteLine($"Detect 返回 {elements.Count} 个元素(窗口PID={_fixture.WindowPid})");
        // 内容级断言(黑帧方法论:断言真实内容,而非仅不抛异常)
        Assert.NotEmpty(elements);
        Assert.All(elements, e => Assert.InRange(e.Depth, 0, 12));  // 深度 ≤12
        Assert.All(elements, e => Assert.True(e.Bounds.Width >= 0 && e.Bounds.Height >= 0));

        // 根元素(depth 0)存在;类名/窗口名能对上记事本(经典版 class=Notepad,商店版同名)
        var root = elements.FirstOrDefault(e => e.Depth == 0);
        Assert.NotNull(root);
        var rootMatches =
            root.ClassName.Contains("Notepad", StringComparison.OrdinalIgnoreCase) ||
            root.Name.Contains("Notepad", StringComparison.OrdinalIgnoreCase) ||
            root.Name.Contains("记事本", StringComparison.Ordinal) ||
            root.Name.Contains("无标题", StringComparison.Ordinal) ||
            root.Name.Contains("Untitled", StringComparison.OrdinalIgnoreCase);
        Assert.True(rootMatches, $"根元素对不上记事本:class={root.ClassName} name={root.Name}");

        // 编辑区存在(经典=Edit,商店版=Document)
        Assert.Contains(elements, e => e.ControlType is "Edit" or "Document");

        // 缓存一致性:Invalidate 后重新填充,再取应命中缓存且内容一致
        detector.Invalidate(hwnd);
        var again = detector.Detect(hwnd);
        Assert.NotEmpty(again);
        var sw2 = Stopwatch.StartNew();
        var cached = detector.Detect(hwnd);
        sw2.Stop();
        Assert.Equal(again.Count, cached.Count);
        Assert.True(sw2.ElapsedMilliseconds < 50, $"缓存命中应 <50ms,实际 {sw2.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Notepad_HitTest_ReturnsLeafToRootChain()
    {
        if (_fixture.Unavailable)
        {
            _output.WriteLine("环境无记事本,跳过。");
            return;
        }
        var hwnd = _fixture.Hwnd;
        if (!NativeMethodsProxy.GetWindowRect(hwnd, out var rect) || rect.Width <= 0)
        {
            return;
        }
        using var detector = new ElementDetector();
        var center = new PxPoint(
            rect.X + rect.Width / 2,
            rect.Y + rect.Height / 2);
        // 同 Detect:首调可能超时返回空,重试等待缓存
        IReadOnlyList<ElementInfo> chain = Array.Empty<ElementInfo>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            chain = detector.HitTest(center);
            if (chain.Count > 0)
            {
                break;
            }
            Thread.Sleep(150);
        }

        _output.WriteLine($"HitTest({center.X},{center.Y}) 返回 {chain.Count} 层");
        Assert.NotEmpty(chain);
        // 叶→根:第一个深度最大,最后一个为顶层窗口(depth 0)
        Assert.True(chain[0].Depth >= chain[^1].Depth, "链应 叶→根 排列");
        Assert.Equal(0, chain[^1].Depth);
        // 叶元素 Bounds 应包含命中点(物理像素)
        Assert.True(chain[0].Bounds.Contains(center.X, center.Y),
            $"叶 Bounds {chain[0].Bounds} 应包含点 ({center.X},{center.Y})");
        Assert.InRange(chain.Count, 1, 24);
    }

    [Fact]
    public void Detect_ConcurrentCalls_ThreadIsolation()
    {
        if (_fixture.Unavailable)
        {
            _output.WriteLine("环境无记事本,跳过。");
            return;
        }
        var hwnd = _fixture.Hwnd;
        using var detector = new ElementDetector();
        var errors = new List<Exception>();
        var threads = new List<Thread>();
        for (int t = 0; t < 4; t++)
        {
            var th = new Thread(() =>
            {
                try
                {
                    // 契约:单次调用 ≤100ms 可能返回空(超时);重试至多 10s 内应拿到真实结果
                    var deadline = Stopwatch.StartNew();
                    var got = false;
                    while (deadline.ElapsedMilliseconds < 10000 && !got)
                    {
                        var r = detector.Detect(hwnd);
                        if (r.Count > 0)
                        {
                            got = true;
                        }
                        else
                        {
                            Thread.Sleep(100);
                        }
                    }
                    if (!got)
                    {
                        lock (errors) { errors.Add(new TimeoutException("10s 内未拿到非空元素")); }
                    }
                }
                catch (Exception ex)
                {
                    lock (errors) { errors.Add(ex); }
                }
            });
            threads.Add(th);
        }
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join(30000));
        Assert.Empty(errors); // 并发下异常全吞:不崩、不外抛
    }
}

/// <summary>不依赖真实窗口的检测器基础行为(无 UI)。</summary>
public class ElementDetectorBasicTests
{
    [Fact]
    public void Detect_InvalidHwnd_ReturnsEmptyFast()
    {
        using var detector = new ElementDetector();
        var sw = Stopwatch.StartNew();
        var result = detector.Detect(new IntPtr(0x12345678)); // 不存在的 HWND
        sw.Stop();
        Assert.Empty(result);
        Assert.True(sw.ElapsedMilliseconds < 500, $"非法 HWND 应快速返回,实际 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void HitTest_Offscreen_ReturnsEmpty_WithoutCrash()
    {
        using var detector = new ElementDetector();
        var result = detector.HitTest(new PxPoint(-100000, -100000));
        // 屏外点:空链或桌面链均可,关键是不崩、在预算内返回
        Assert.True(result.Count == 0 || result[^1].Depth == 0);
    }

    [Fact]
    public void Invalidate_UnknownHwnd_IsNoOp()
    {
        using var detector = new ElementDetector();
        detector.Invalidate(new IntPtr(0x12345678)); // 不抛即通过
    }
}

/// <summary>测试专用桥(避免把 internal 扩散到生产类)。</summary>
internal static class NativeMethodsProxy
{
    public static bool IsWindow(IntPtr hwnd) => Calls.IsWindow(hwnd);

    public static IntPtr FindWindowByClass(string className) => Calls.FindWindowW(className, null);

    public static void PostWmClose(IntPtr hwnd)
    {
        _ = Calls.PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }

    public static uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid)
    {
        pid = 0;
        return Calls.GetWindowThreadProcessId(hwnd, ref pid);
    }

    public static bool GetWindowRect(IntPtr hwnd, out OpenSnip.Core.PxRect rect)
    {
        rect = default;
        if (!Calls.GetWindowRect(hwnd, out var r))
        {
            return false;
        }
        rect = new OpenSnip.Core.PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return true;
    }

    private static class Calls
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, ref uint pid);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect lpRect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
