using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenSnip.Core;

/// <summary>
/// 检测到的 UIA 元素(物理像素 Bounds)。
/// WindowHandle = 所属顶层窗口句柄(Detect=查询入参;HitTest=元素自身或最近有句柄的祖先)。
/// Depth = 距顶层窗口元素的层数(窗口=0)。
/// </summary>
public sealed record ElementInfo(IntPtr WindowHandle, PxRect Bounds, string Name, string ClassName, string ControlType, int Depth);

/// <summary>
/// 元素检测 v1(契约 §10):窗口内 UIA 树扁平化缓存 / 点命中叶→根链 / 失效。
/// <para>
/// 契约红线:UIA 一切调用隔离在专用 MTA 工作线程,单调用 100ms 超时(超时先返回空,结果完成后进缓存),
/// 任何异常全部吞掉返回空——绝不带崩 OpenSnip。
/// 遍历:ControlViewWalker DFS,深度 ≤12、节点 ≤2000、worker 内预算 90ms。
/// </para>
/// </summary>
public sealed class ElementDetector : IDisposable
{
    private const int MaxDepth = 12;
    private const int MaxNodes = 2000;
    private const int BudgetMs = 90;      // worker 内部预算(留给 100ms 超时等待的余量)
    private const int ChainMax = 24;      // HitTest 叶→根链长度上限
    private const int CallTimeoutMs = 100;

    private sealed record Job(nint Hwnd, PxPoint Point, bool IsHit, TaskCompletionSource<IReadOnlyList<ElementInfo>> Tcs);

    private static readonly IReadOnlyList<ElementInfo> Empty = Array.Empty<ElementInfo>();

    // UIA_CONTROLTYPE_ID → 名称(SDK UIAutomationClient.h 权威映射)
    private static readonly Dictionary<int, string> ControlTypeNames = new()
    {
        { 50000, "Button" }, { 50001, "Calendar" }, { 50002, "CheckBox" }, { 50003, "ComboBox" },
        { 50004, "Edit" }, { 50005, "Hyperlink" }, { 50006, "Image" }, { 50007, "ListItem" },
        { 50008, "List" }, { 50009, "Menu" }, { 50010, "MenuBar" }, { 50011, "MenuItem" },
        { 50012, "ProgressBar" }, { 50013, "RadioButton" }, { 50014, "ScrollBar" }, { 50015, "Slider" },
        { 50016, "Spinner" }, { 50017, "StatusBar" }, { 50018, "Tab" }, { 50019, "TabItem" },
        { 50020, "Text" }, { 50021, "ToolBar" }, { 50022, "ToolTip" }, { 50023, "Tree" },
        { 50024, "TreeItem" }, { 50025, "Custom" }, { 50026, "Group" }, { 50027, "Thumb" },
        { 50028, "DataGrid" }, { 50029, "DataItem" }, { 50030, "Document" }, { 50031, "SplitButton" },
        { 50032, "Window" }, { 50033, "Pane" }, { 50034, "Header" }, { 50035, "HeaderItem" },
        { 50036, "Table" }, { 50037, "TitleBar" }, { 50038, "Separator" }, { 50039, "SemanticZoom" },
        { 50040, "AppBar" },
    };

    private readonly object _gate = new();
    private readonly Dictionary<nint, IReadOnlyList<ElementInfo>> _cache = new();
    private readonly BlockingCollection<Job> _queue = new();
    private Thread? _worker;
    private bool _disposed;

    public ElementDetector()
    {
    }

    /// <summary>
    /// 窗口内 UIA 树扁平化(缓存)。命中缓存直接返回;未命中投递 worker 并最多等 100ms。
    /// </summary>
    public IReadOnlyList<ElementInfo> Detect(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return Empty;
        }
        lock (_gate)
        {
            if (_cache.TryGetValue(windowHandle, out var hit))
            {
                return hit;
            }
        }
        if (!NativeMethods.IsWindow(windowHandle))
        {
            PerfLog.Log("Core", $"ElementDetector.Detect:HWND 无效(窗口已销毁?),返回空 hwnd={windowHandle}");
            return Empty;
        }
        return PostAndWait(new Job(windowHandle, default, false, CreateTcs()));
    }

    /// <summary>点命中:叶→根元素链([0]=叶,最后一个=顶层窗口),空=无。</summary>
    public IReadOnlyList<ElementInfo> HitTest(PxPoint screenPt)
    {
        return PostAndWait(new Job(0, screenPt, true, CreateTcs()));
    }

    /// <summary>失效指定窗口的扁平化缓存(纯数据操作,无需 UIA 线程)。</summary>
    public void Invalidate(IntPtr windowHandle)
    {
        lock (_gate)
        {
            _cache.Remove(windowHandle);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cache.Clear();
        }
        _queue.CompleteAdding();
        if (_worker is { } w && !w.Join(1000))
        {
            // 工作线程卡在 UIA 调用里:后台线程,不杀(契约:绝不带崩进程),随进程退出
            PerfLog.Log("Core", "ElementDetector 工作线程未在 1s 内退出(可能卡在 UIA 调用),放弃等待。");
        }
        _queue.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TaskCompletionSource<IReadOnlyList<ElementInfo>> CreateTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IReadOnlyList<ElementInfo> PostAndWait(Job job)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Empty;
            }
            EnsureWorker();
        }
        try
        {
            _queue.Add(job);
        }
        catch (Exception ex)
        {
            PerfLog.Log("Core", $"ElementDetector 投递失败(已吞掉):{ex.Message}");
            return Empty;
        }
        if (job.Tcs.Task.Wait(CallTimeoutMs))
        {
            return job.Tcs.Task.Result;
        }
        // 契约超时:先返回空;worker 完成后结果进缓存,下次 Detect 命中
        PerfLog.Log("Core", $"ElementDetector 调用超时(>100ms),返回空(完成后进缓存)");
        return Empty;
    }

    private void EnsureWorker()
    {
        if (_worker is not null)
        {
            return;
        }
        _worker = new Thread(WorkerProc)
        {
            Name = "OpenSnip.UIA",
            IsBackground = true,
        };
        _worker.Start();
    }

    private void WorkerProc()
    {
        _ = UiaInterop.InitializeCom();

        var automation = UiaInterop.CreateAutomation();
        IUIAutomationTreeWalker? walker = null;
        try
        {
            walker = automation?.GetControlViewWalker();
        }
        catch (Exception ex)
        {
            PerfLog.Log("Core", $"UIA TreeWalker 获取失败(已吞掉,元素检测返回空):{ex.Message}");
        }

        try
        {
            foreach (var job in _queue.GetConsumingEnumerable())
            {
                IReadOnlyList<ElementInfo> result = Empty;
                if (automation is null || walker is null)
                {
                    job.Tcs.TrySetResult(Empty);
                    continue;
                }
                var sw = Stopwatch.StartNew();
                try
                {
                    result = job.IsHit
                        ? HitTestCore(automation, walker, job.Point, sw)
                        : DetectCore(automation, walker, job.Hwnd, sw);
                }
                catch (Exception ex)
                {
                    // 契约红线:UIA 异常全吞,返回空
                    PerfLog.Log("Core", $"ElementDetector UIA 异常(已吞掉):{ex.Message}");
                    result = Empty;
                }
                if (!job.IsHit && job.Hwnd != 0 && result.Count > 0)
                {
                    lock (_gate)
                    {
                        _cache[job.Hwnd] = result;
                    }
                }
                job.Tcs.TrySetResult(result);
            }
        }
        catch (ObjectDisposedException)
        {
            // 队列已释放:正常退出
        }
    }

    /// <summary>窗口 UIA 树扁平化:ControlViewWalker DFS,深度 ≤12、节点 ≤2000、预算 90ms。</summary>
    private static IReadOnlyList<ElementInfo> DetectCore(
        IUIAutomation automation, IUIAutomationTreeWalker walker, nint hwnd, Stopwatch sw)
    {
        var root = automation.ElementFromHandle(hwnd);
        if (root is null)
        {
            return Empty;
        }
        var list = new List<ElementInfo>(128);
        var stack = new Stack<(IUIAutomationElement Elem, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && list.Count < MaxNodes && sw.ElapsedMilliseconds < BudgetMs)
        {
            var (elem, depth) = stack.Pop();
            var info = TryBuildInfo(elem, hwnd, depth);
            if (info is not null)
            {
                list.Add(info);
            }
            if (depth >= MaxDepth)
            {
                continue;
            }
            var child = SafeChild(walker.GetFirstChildElement, elem);
            var children = new List<IUIAutomationElement>(8);
            while (child is not null && children.Count < MaxNodes && sw.ElapsedMilliseconds < BudgetMs)
            {
                children.Add(child);
                child = SafeChild(walker.GetNextSiblingElement, child);
            }
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], depth + 1));
            }
        }
        return list;
    }

    /// <summary>点命中:ElementFromPoint 取叶,沿 ControlViewWalker 上行,输出 叶→根 链。</summary>
    private static IReadOnlyList<ElementInfo> HitTestCore(
        IUIAutomation automation, IUIAutomationTreeWalker walker, PxPoint pt, Stopwatch sw)
    {
        var leaf = automation.ElementFromPoint(new UiaPoint(pt.X, pt.Y));
        if (leaf is null)
        {
            return Empty;
        }
        // 上行链:up[0]=叶 … up[^1]=桌面根
        var up = new List<IUIAutomationElement>(16) { leaf };
        var cur = leaf;
        while (up.Count < ChainMax + 2 && sw.ElapsedMilliseconds < BudgetMs)
        {
            var parent = SafeChild(walker.GetParentElement, cur);
            if (parent is null)
            {
                break;
            }
            up.Add(parent);
            cur = parent;
        }
        int winIdx = up.Count - 2; // up[^1]=桌面根(排除),顶层窗口=up[^2]
        if (winIdx < 0)
        {
            return Empty; // 直接命中桌面根,无窗口元素
        }
        // 窗口句柄继承:自顶向下,元素自身无句柄则继承最近有句柄的祖先
        var hwnds = new nint[winIdx + 1];
        nint last = 0;
        for (int i = winIdx; i >= 0; i--)
        {
            var own = SafeHwnd(up[i]);
            if (own != IntPtr.Zero)
            {
                last = own;
            }
            hwnds[i] = last;
        }
        // 叶→根 输出;depth 与 Detect 语义一致(顶层窗口=0,越深越大)
        var result = new List<ElementInfo>(winIdx + 1);
        for (int i = 0; i <= winIdx; i++)
        {
            int depth = winIdx - i;
            var info = TryBuildInfo(up[i], hwnds[i], depth)
                       ?? new ElementInfo(hwnds[i], default, string.Empty, string.Empty, "Unknown", depth);
            result.Add(info);
        }
        return result;
    }

    private static ElementInfo? TryBuildInfo(IUIAutomationElement elem, nint hwnd, int depth)
    {
        try
        {
            var r = elem.CurrentBoundingRectangle;
            string name = elem.CurrentName ?? string.Empty;
            string cls = elem.CurrentClassName ?? string.Empty;
            int ct = elem.CurrentControlType;
            string controlType = ControlTypeNames.TryGetValue(ct, out var n) ? n : $"Unknown({ct})";
            return new ElementInfo(
                hwnd,
                new PxRect(r.Left, r.Top, r.Width, r.Height),
                name,
                cls,
                controlType,
                depth);
        }
        catch
        {
            // ELEMENTNOTAVAILABLE / 已销毁等:单个元素失败不拖垮整棵树
            return null;
        }
    }

    private static IUIAutomationElement? SafeChild(
        Func<IUIAutomationElement, IUIAutomationElement> walk, IUIAutomationElement elem)
    {
        try
        {
            return walk(elem);
        }
        catch
        {
            return null;
        }
    }

    private static nint SafeHwnd(IUIAutomationElement elem)
    {
        try
        {
            return elem.CurrentNativeWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
