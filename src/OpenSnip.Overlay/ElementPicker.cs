// M2b 元素检测消费(SPEC §13):Tab 切换 窗口/元素 两模式;
// 元素模式悬停 = 青框高亮(HitTest 链第 index 项),滚轮 = 链上下层级,单击 = 元素 Bounds 设选区。
// 契约:命中函数由 SnipOverlay 构造注入(Func<PxPoint, IReadOnlyList<ElementInfo>>?,可测性);
// 内部 80ms 节流;**空响应 = 检测中超时,属正常**(保持上次高亮 + Detecting 提示,勿当致命)。
using OpenSnip.Core;

namespace OpenSnip.Overlay;

/// <summary>悬停高亮模式。</summary>
internal enum HitMode
{
    Window,   // 蓝框 #00A8FF(§3.2 既有)
    Element,  // 青框 #00E5FF(§13;elementHitTest 注入后 Tab 可切)
}

/// <summary>元素选择器(仅 Overlay UI 线程触碰)。</summary>
internal sealed class ElementPicker
{
    private const int ThrottleMs = 80;

    private readonly Func<PxPoint, IReadOnlyList<ElementInfo>>? _hitTest;
    private IReadOnlyList<ElementInfo> _chain = Array.Empty<ElementInfo>(); // 叶→根链(缓存)
    private int _index;                          // 链内高亮索引(滚轮移动)
    private long _lastQueryTicks;                // 上次真实调用(节流)
    private PxPoint _lastPt;                     // 上次查询点(缓存有效性)

    /// <summary>当前模式。</summary>
    public HitMode Mode { get; private set; } = HitMode.Window;

    /// <summary>当前高亮元素(空响应保持上次;Window 模式下无意义)。</summary>
    public ElementInfo? Highlighted { get; private set; }

    /// <summary>最近一次命中为空(检测中超时)——UI 显示"检测中"提示。</summary>
    public bool Detecting { get; private set; }

    public ElementPicker(Func<PxPoint, IReadOnlyList<ElementInfo>>? hitTest)
    {
        _hitTest = hitTest;
    }

    /// <summary>元素检测可用性(未注入 = 无元素模式)。</summary>
    public bool Available => _hitTest is not null;

    /// <summary>Tab 切换模式。返回 false = 不可切换(未注入元素检测)。</summary>
    public bool ToggleMode()
    {
        if (!Available) return false;
        Mode = Mode == HitMode.Window ? HitMode.Element : HitMode.Window;
        if (Mode == HitMode.Window) { Highlighted = null; Detecting = false; }
        return true;
    }

    /// <summary>
    /// 悬停移动(Hover + 元素模式)。80ms 节流:窗口期内复用缓存链;超时才真实调用(最多阻塞 100ms)。
    /// 返回 true = 高亮发生变化(调用方重绘)。
    /// </summary>
    public bool OnMouseMove(PxPoint pt)
    {
        if (Mode != HitMode.Element || _hitTest is null) return false;

        long now = Environment.TickCount64;
        bool throttled = now - _lastQueryTicks < ThrottleMs;
        if (throttled && _lastPt == pt) return false;   // 同点且节流窗口内:无变化

        if (!throttled)
        {
            _lastQueryTicks = now;
            _lastPt = pt;
            try
            {
                _chain = _hitTest(pt) ?? Array.Empty<ElementInfo>();
            }
            catch
            {
                _chain = Array.Empty<ElementInfo>(); // 注入方异常 = 空响应语义,软处理
            }
            if (_chain.Count == 0)
            {
                Detecting = true;                 // 超时空:保持上次高亮 + 提示
                return false;
            }
            Detecting = false;
            _index = Math.Clamp(_index, 0, _chain.Count - 1);
        }

        var cur = _chain.Count > 0 ? _chain[Math.Clamp(_index, 0, _chain.Count - 1)] : null;
        bool changed = !Equals(cur, Highlighted);
        Highlighted = cur;
        return changed;
    }

    /// <summary>
    /// 滚轮:链内层级切换(下 = 叶→根方向)。返回 true = 高亮变化。
    /// </summary>
    public bool OnWheel(int delta)
    {
        if (Mode != HitMode.Element || _chain.Count == 0) return false;
        int next = Math.Clamp(_index + (delta > 0 ? 1 : -1), 0, _chain.Count - 1);
        if (next == _index) return false;
        _index = next;
        Highlighted = _chain[_index];
        Detecting = false;
        return true;
    }

    /// <summary>单击:取当前高亮元素(设选区用)。返回 null = 无高亮(不消费)。</summary>
    public ElementInfo? TakeClickSelection()
    {
        if (Mode != HitMode.Element) return null;
        var el = Highlighted;
        Highlighted = null; // 选区已定,清悬停
        return el;
    }
}
