// 交互会话状态机 + 选区几何 + 裁剪(仅 Overlay 的 STA 线程访问)。
// 状态:Hover(空闲悬停,窗口高亮)→ 拖拽初选 → Selected(锚点调整/整体移动/方向键微调)。
using System.Collections.Generic;
using OpenSnip.Core;
using OpenSnip.Overlay.Interop;

namespace OpenSnip.Overlay;

internal enum SnipPhase { Hover, Selected }

internal enum DragKind
{
    None,       // 无拖拽(窗口高亮 + 放大镜可见)
    Create,     // 拖拽新建选区
    Move,       // 拖拽整体移动选区
    Anchor,     // 拖拽 8 锚点之一调整尺寸
}

/// <summary>交互会话。字段与方法只允许 Overlay UI 线程触碰。</summary>
internal sealed class OverlaySession
{
    private const float DragThresholdPx = 3f;   // 位移小于此值视为"单击"
    private const float AnchorHitRadiusPx = 7f; // 锚点命中半径(物理 px)

    private readonly IReadOnlyList<DisplayInfo> _displays;
    private readonly Dictionary<int, NativeBitmap?> _frozen;   // DisplayInfo.Index → 冻结位图(可空)
    private readonly HashSet<IntPtr> _selfHwnds;               // 自身遮罩窗口,窗口命中检测需过滤
    private readonly WindowHitTester? _hitTester;
    private readonly PxRect _virtualDesktop;                   // 各屏 Bounds 包围盒(移动选区时夹取)

    // —— 悬停窗口(§2.4:HitTopToBottom 列表 + 滚轮切层级)——
    private IReadOnlyList<WindowInfo> _hits = Array.Empty<WindowInfo>();
    private int _hoverIndex;
    private IntPtr _lastTopWindow;

    // —— 拖拽中间态 ——
    private PxPoint _dragStartPt;
    private PxRect _dragStartRect;
    private int _anchorIndex = -1;

    public SnipPhase Phase { get; private set; } = SnipPhase.Hover;
    public PxRect? Selection { get; private set; }
    public PxPoint? Cursor { get; private set; }               // 虚拟桌面物理坐标
    public PxRect? HoverRect { get; private set; }             // 当前高亮窗口矩形
    public DragKind Drag { get; private set; } = DragKind.None;

    /// <summary>放大镜可见性:非拖拽状态可见(Snipaste 风格),任何拖拽中隐藏。</summary>
    public bool WantsMagnifier => Drag == DragKind.None && Cursor is not null;

    public OverlaySession(IReadOnlyList<DisplayInfo> displays,
        Dictionary<int, NativeBitmap?> frozen, HashSet<IntPtr> selfHwnds)
    {
        _displays = displays;
        _frozen = frozen;
        _selfHwnds = selfHwnds;
        _virtualDesktop = ComputeVirtualDesktop(displays);
        try { _hitTester = new WindowHitTester(); }
        catch { _hitTester = null; } // Core 不可用时仅失去窗口高亮,手选流程不受影响
    }

    // —————————————————— 输入入口 ——————————————————

    public void OnMouseMove(PxPoint pt)
    {
        Cursor = pt;
        switch (Drag)
        {
            case DragKind.None when Phase == SnipPhase.Hover:
                ProbeHover(pt);
                break;
            case DragKind.Create:
                Selection = OverlayGeometry.Normalize(_dragStartPt, pt);
                break;
            case DragKind.Move:
                Selection = ClampToDesktop(OverlayGeometry.Offset(_dragStartRect, pt.X - _dragStartPt.X, pt.Y - _dragStartPt.Y));
                break;
            case DragKind.Anchor:
                Selection = OverlayGeometry.AdjustFromAnchor(Selection!.Value, _anchorIndex, pt);
                break;
        }
    }

    public void OnLeftButtonDown(PxPoint pt)
    {
        Cursor = pt;
        switch (Phase)
        {
            case SnipPhase.Hover:
                // 起拖(自由选区);松开时无位移且存在高亮窗口 → 选中窗口矩形
                Drag = DragKind.Create;
                _dragStartPt = pt;
                Selection = null;
                break;

            case SnipPhase.Selected when Selection is { } s:
                if (AnchorHitTest(s, pt) is { } idx)
                {
                    Drag = DragKind.Anchor;
                    _anchorIndex = idx;
                }
                else if (s.Contains(pt.X, pt.Y))
                {
                    // 选区内按下 → 整体移动
                    Drag = DragKind.Move;
                    _dragStartRect = s;
                    _dragStartPt = pt;
                }
                else
                {
                    // 选区外按下 → 重新起选(Snipaste 行为)
                    Phase = SnipPhase.Hover;
                    Drag = DragKind.Create;
                    _dragStartPt = pt;
                    Selection = null;
                }
                break;
        }
    }

    public void OnLeftButtonUp(PxPoint pt)
    {
        Cursor = pt;
        if (Drag == DragKind.Create)
        {
            var r = OverlayGeometry.Normalize(_dragStartPt, pt);
            bool tiny = r.Width < DragThresholdPx && r.Height < DragThresholdPx;
            if (tiny)
            {
                // 单击:有高亮窗口 → 选中该窗口;否则回到空闲
                if (Phase == SnipPhase.Hover && HoverRect is { } hr)
                {
                    Selection = hr;
                    Phase = SnipPhase.Selected;
                }
                else
                {
                    Selection = null;
                    Phase = SnipPhase.Hover;
                }
            }
            else
            {
                Selection = r;
                Phase = SnipPhase.Selected;
            }
        }
        Drag = DragKind.None;
        _anchorIndex = -1;
        if (Phase == SnipPhase.Hover) ProbeHover(pt); // 拖拽结束恢复窗口高亮
    }

    /// <summary>
    /// 测试驱动(SPEC §8.1):等价"拖拽完成后"的选区状态——复用既有状态机字段与虚拟桌面夹取,
    /// 置为 Selected 相位(重绘出边框/锚点/尺寸标签)。w/h 非正返回 false。
    /// </summary>
    public bool SetRegionExternal(PxRect r)
    {
        if (r.Width < 1 || r.Height < 1) return false;
        Selection = ClampToDesktop(r);
        Phase = SnipPhase.Selected;
        Drag = DragKind.None;
        _anchorIndex = -1;
        _hits = Array.Empty<WindowInfo>(); // 清悬停层级栈;回到 Hover 时会重新探测
        HoverRect = null;
        return true;
    }

    /// <summary>双击/回车且无选区时:把高亮窗口选为当前选区(Snipaste 行为)。</summary>
    public bool TrySelectHover()
    {
        if (HoverRect is not { } hr) return false;
        Selection = hr;
        Phase = SnipPhase.Selected;
        return true;
    }

    /// <summary>方向键微调选区 1px(Shift = 10px)。无选区时返回 false。</summary>
    public bool Nudge(int dx, int dy)
    {
        if (Phase != SnipPhase.Selected || Selection is not { } s) return false;
        Selection = ClampToDesktop(OverlayGeometry.Offset(s, dx, dy));
        return true;
    }

    /// <summary>滚轮:切换高亮窗口的 Z 层级(§2.4 用整个命中列表)。返回 true 表示有变化。</summary>
    public bool OnWheel(int delta)
    {
        if (_hits.Count == 0) return false;
        _hoverIndex = Math.Clamp(_hoverIndex + (delta < 0 ? 1 : -1), 0, _hits.Count - 1);
        HoverRect = _hits[_hoverIndex].Bounds;
        return true;
    }

    /// <summary>按光标位置/拖拽形态给出标准光标 OCR id。</summary>
    public int PickCursorId()
    {
        if (Drag == DragKind.Anchor)
            return OverlayGeometry.AnchorCursorIds[_anchorIndex];

        if (Selection is { } s && Cursor is { } c)
        {
            var idx = AnchorHitTest(s, c);
            if (idx is { } i) return OverlayGeometry.AnchorCursorIds[i];
            if (s.Contains(c.X, c.Y)) return Win32.OCR_SIZEALL;
        }
        return Win32.OCR_CROSS;
    }

    /// <summary>放大镜中心像素 "#RRGGBB"(直读冻结位图;不可得时 null)。</summary>
    public unsafe string? GetMagnifierHex()
    {
        if (Cursor is not { } pt) return null;
        var d = FindDisplay(pt);
        if (d is null) return null;
        if (!_frozen.TryGetValue(d.Index, out var bmp) || bmp is null) return null;

        int lx = Math.Clamp(pt.X - d.Bounds.X, 0, bmp.Width - 1);
        int ly = Math.Clamp(pt.Y - d.Bounds.Y, 0, bmp.Height - 1);
        byte* p = (byte*)bmp.Scan0 + (long)ly * bmp.Stride + (long)lx * 4;
        byte b = p[0], g = p[1], r = p[2];
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>构建某窗口的渲染模型。</summary>
    public RenderModel BuildModel(OverlayWindow w)
    {
        bool highlight = Phase == SnipPhase.Hover && Drag == DragKind.None;
        return new RenderModel(
            WindowOrigin: new PxPoint(w.Display.Bounds.X, w.Display.Bounds.Y),
            WindowWidth: w.Width,
            WindowHeight: w.Height,
            Frozen: w.FrozenImage,
            Selection: Selection,
            HighlightWindow: highlight ? HoverRect : null,
            CursorPt: Cursor,
            MagnifierHex: WantsMagnifier ? GetMagnifierHex() : null,
            ShowMagnifier: WantsMagnifier,
            DpiScale: w.Display.DpiScale);
    }

    /// <summary>
    /// 确认裁剪:选区 → NativeBitmap(物理 px,非预乘 BGRA32)。
    /// 选区完整落在单屏内走 Core 契约 CopyRegionFrom;跨屏时按行内存拷贝拼接
    /// (契约 CopyRegionFrom 仅支持 (0,0) 落点,无法直接跨屏拼图)。
    /// </summary>
    public SnipResult? BuildResult()
    {
        if (Selection is not { } sel || sel.Width < 1 || sel.Height < 1) return null;

        // 预扫描:任一相交屏缺少冻结画面 → 整图先清零(Allocate 不保证零初始化)
        bool needClear = false;
        foreach (var d in _displays)
        {
            var region = OverlayGeometry.Intersect(sel, d.Bounds);
            if (region.IsEmpty) continue;
            if (!_frozen.TryGetValue(d.Index, out var f) || f is null) { needClear = true; break; }
        }

        var result = NativeBitmap.Allocate(sel.Width, sel.Height);
        if (needClear) ClearBitmap(result);

        foreach (var d in _displays)
        {
            var region = OverlayGeometry.Intersect(sel, d.Bounds);
            if (region.IsEmpty) continue;
            if (!_frozen.TryGetValue(d.Index, out var src) || src is null)
                continue; // 该屏捕获失败 → 保持清零后的黑

            if (region.Width == sel.Width && region.Height == sel.Height)
            {
                // 单屏整选区:契约路径
                result.CopyRegionFrom(src, sel.X - d.Bounds.X, sel.Y - d.Bounds.Y);
            }
            else
            {
                CopyRegionRows(result, sel, src, d, region);
            }
        }

        return new SnipResult(result, sel, PickPrimaryDisplay(sel));
    }

    // —————————————————— 内部 ——————————————————

    private static unsafe void ClearBitmap(NativeBitmap bmp)
    {
        byte* p = (byte*)bmp.Scan0;
        long total = (long)bmp.Stride * bmp.Height;
        System.Runtime.CompilerServices.Unsafe.InitBlockUnaligned(ref *p, 0, (uint)total);
    }

    private static unsafe void CopyRegionRows(NativeBitmap dst, PxRect sel, NativeBitmap src,
        DisplayInfo d, PxRect region)
    {
        // 跨屏拼接:逐行 memcpy(源/目标 stride 可能不同)
        int rowBytes = region.Width * 4;
        byte* dstBase = (byte*)dst.Scan0 + (long)(region.Y - sel.Y) * dst.Stride + (long)(region.X - sel.X) * 4;
        byte* srcBase = (byte*)src.Scan0 + (long)(region.Y - d.Bounds.Y) * src.Stride + (long)(region.X - d.Bounds.X) * 4;
        for (int y = 0; y < region.Height; y++)
        {
            Buffer.MemoryCopy(srcBase + (long)y * src.Stride, dstBase + (long)y * dst.Stride, rowBytes, rowBytes);
        }
    }

    private void ProbeHover(PxPoint pt)
    {
        if (_hitTester is null) { HoverRect = null; return; }

        _hits = FilterSelf(_hitTester.HitTopToBottom(pt));
        if (_hits.Count == 0) { HoverRect = null; _lastTopWindow = IntPtr.Zero; return; }

        // 光标换到别的顶层窗口后重置层级偏移
        if (_hits[0].Handle != _lastTopWindow)
        {
            _lastTopWindow = _hits[0].Handle;
            _hoverIndex = 0;
        }
        _hoverIndex = Math.Clamp(_hoverIndex, 0, _hits.Count - 1);
        HoverRect = _hits[_hoverIndex].Bounds;
    }

    private IReadOnlyList<WindowInfo> FilterSelf(IReadOnlyList<WindowInfo> hits)
    {
        List<WindowInfo>? filtered = null;
        for (int i = 0; i < hits.Count; i++)
        {
            if (_selfHwnds.Contains(hits[i].Handle))
            {
                filtered ??= new List<WindowInfo>(hits.Count);
                for (int j = 0; j < i; j++) filtered.Add(hits[j]);
                for (int j = i + 1; j < hits.Count; j++)
                    if (!_selfHwnds.Contains(hits[j].Handle)) filtered.Add(hits[j]);
                return filtered;
            }
        }
        return hits; // 无自身窗口:原样返回,零分配
    }

    private int? AnchorHitTest(PxRect r, PxPoint pt)
    {
        var anchors = OverlayGeometry.AnchorCenters(r);
        float scale = FindDisplay(pt)?.DpiScale ?? 1f;
        float hit = MathF.Max(AnchorHitRadiusPx, AnchorHitRadiusPx * MathF.Max(1f, scale));
        for (int i = 0; i < anchors.Length; i++)
        {
            if (MathF.Abs(anchors[i].X - pt.X) <= hit && MathF.Abs(anchors[i].Y - pt.Y) <= hit)
                return i;
        }
        return null;
    }

    private DisplayInfo? FindDisplay(PxPoint pt)
    {
        foreach (var d in _displays)
            if (d.Bounds.Contains(pt.X, pt.Y)) return d;
        return null;
    }

    private DisplayInfo PickPrimaryDisplay(PxRect sel)
    {
        int cx = sel.X + sel.Width / 2, cy = sel.Y + sel.Height / 2;
        foreach (var d in _displays)
            if (d.Bounds.Contains(cx, cy)) return d;                         // 主判定:选区中心所在屏
        foreach (var d in _displays)
            if (!OverlayGeometry.Intersect(sel, d.Bounds).IsEmpty) return d; // 次选:第一个相交屏
        return _displays[0];
    }

    private PxRect ClampToDesktop(PxRect r)
    {
        int maxX = Math.Max(_virtualDesktop.X, _virtualDesktop.X + _virtualDesktop.Width - r.Width);
        int maxY = Math.Max(_virtualDesktop.Y, _virtualDesktop.Y + _virtualDesktop.Height - r.Height);
        int x = Math.Clamp(r.X, _virtualDesktop.X, maxX);
        int y = Math.Clamp(r.Y, _virtualDesktop.Y, maxY);
        return new PxRect(x, y, r.Width, r.Height);
    }

    internal static PxRect ComputeVirtualDesktop(IReadOnlyList<DisplayInfo> displays)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var d in displays)
        {
            minX = Math.Min(minX, d.Bounds.X);
            minY = Math.Min(minY, d.Bounds.Y);
            maxX = Math.Max(maxX, d.Bounds.X + d.Bounds.Width);
            maxY = Math.Max(maxY, d.Bounds.Y + d.Bounds.Height);
        }
        return new PxRect(minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>选区几何工具(PxRect 约定:X/Y 左上角,W/H 为像素数,Contains 为 [X, X+W) 半开区间)。</summary>
internal static class OverlayGeometry
{
    /// <summary>8 锚点索引:0=NW 1=N 2=NE 3=W 4=E 5=SW 6=S 7=SE。</summary>
    public static PxPoint[] AnchorCenters(PxRect r)
    {
        int l = r.X, t = r.Y;
        int rt = r.X + r.Width, b = r.Y + r.Height;
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return new PxPoint[8]
        {
            new(l, t), new(cx, t), new(rt, t),
            new(l, cy), new(rt, cy),
            new(l, b), new(cx, b), new(rt, b),
        };
    }

    /// <summary>锚点对应的标准光标(与 AnchorCenters 索引对应)。</summary>
    public static readonly int[] AnchorCursorIds =
    {
        Win32.OCR_SIZENWSE, Win32.OCR_SIZENS, Win32.OCR_SIZENESW,
        Win32.OCR_SIZEWE, Win32.OCR_SIZEWE,
        Win32.OCR_SIZENESW, Win32.OCR_SIZENS, Win32.OCR_SIZENWSE,
    };

    /// <summary>两点归一化为矩形(拖拽方向任意,含端点 +1)。</summary>
    public static PxRect Normalize(PxPoint a, PxPoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);

    public static PxRect Offset(PxRect r, int dx, int dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    /// <summary>按锚点调整:锚点所在边跟随光标,其余边固定,允许跨越翻转。</summary>
    public static PxRect AdjustFromAnchor(PxRect r, int anchor, PxPoint pt)
    {
        int l = (anchor is 0 or 3 or 5) ? pt.X : r.X;                // NW/W/SW 拖左缘
        int rt = (anchor is 2 or 4 or 7) ? pt.X + 1 : r.X + r.Width; // NE/E/SE 拖右缘(半开区间)
        int t = (anchor is 0 or 1 or 2) ? pt.Y : r.Y;                // NW/N/NE 拖上缘
        int b = (anchor is 5 or 6 or 7) ? pt.Y + 1 : r.Y + r.Height; // SW/S/SE 拖下缘
        if (l > rt) (l, rt) = (rt, l);
        if (t > b) (t, b) = (b, t);
        return new PxRect(l, t, Math.Max(1, rt - l), Math.Max(1, b - t));
    }

    public static PxRect Intersect(PxRect a, PxRect b)
    {
        int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width);
        int bt = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return (r <= l || bt <= t) ? PxRect.Empty : new PxRect(l, t, r - l, bt - t);
    }
}
