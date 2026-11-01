// M2a 标注交互控制器(SPEC §9):拖拽绘制 / 完成后单击选中(高亮框+8 锚点)/
// 拖动 / 缩放 / Del 删除 / Ctrl+Z/Y 撤销重做(深 100)/ Ctrl+Shift+Z 清空 /
// 二次编辑(选中后同类型工具拖拽 = 改几何;改色/粗细作用于选中形状)。
// 由 OverlaySession 路由(Selected 相位优先消费),全部桌面物理 px,仅 UI 线程。
using OpenSnip.Core;

namespace OpenSnip.Overlay.Annotation;

internal enum AnnotDragKind
{
    None,
    Draw,        // 绘制中(draft)
    MoveShape,   // 拖动选中形状
    ResizeShape, // 锚点缩放选中形状
    RedrawShape, // 二次编辑:同类型工具重画选中形状几何
}

internal sealed class AnnotationController
{
    private const float ClickThresholdPx = 3f;

    public AnnotDocument Document { get; } = new();
    public AnnotHistory History { get; } = new();
    public AnnotToolbar Toolbar { get; } = new();

    /// <summary>选中形状(null = 无)。</summary>
    public AnnotShape? Selected { get; private set; }

    /// <summary>回放帧索引(0 = 当前 capture 帧)。</summary>
    public int FrameIndex { get; private set; }

    /// <summary>工具条确认/取消按钮(等价 Enter/Esc)。</summary>
    public event Action? ConfirmRequested;
    public event Action? CancelRequested;

    private AnnotShape? _draft;
    private AnnotDragKind _drag = AnnotDragKind.None;
    private PxPoint _startPt;
    private PxRect _startBounds;
    private int _anchor = -1;
    private PxRect _selection;
    private int _virtualW, _virtualH;
    private float _dpi = 1f;

    // —————————————————— 会话联动 ——————————————————

    /// <summary>选区/窗口元数据变化(初选完成、拖动、缩放、微调)时调用:重排工具条。</summary>
    public void OnSelectionChanged(PxRect selection, int virtualWidth, int virtualHeight, float dpi)
    {
        _selection = selection;
        _virtualW = virtualWidth;
        _virtualH = virtualHeight;
        _dpi = dpi;
        Toolbar.Layout(selection, virtualWidth, virtualHeight, dpi);
    }

    /// <summary>回放帧索引由 SnipOverlay 切换后同步(仅记录,切图在 SnipOverlay)。</summary>
    public void OnFrameSwitched(int index) => FrameIndex = index;

    // —————————————————— 鼠标 ——————————————————

    /// <summary>按下。返回 true = 已消费(不再走 M1 路由)。</summary>
    public bool HandleMouseDown(PxPoint pt)
    {
        // 1) 工具条(最优先)
        if (Toolbar.Visible)
        {
            var hit = Toolbar.HitTest(pt);
            if (hit.Action != AnToolbarAction.None)
            {
                ExecuteToolbar(hit);
                return true;
            }
        }

        // 2) 选中形状锚点 → 缩放
        if (Selected is { } sel)
        {
            var anchors = OverlayGeometry.AnchorCenters(sel.Bounds);
            for (int i = 0; i < anchors.Length; i++)
            {
                if (MathF.Abs(anchors[i].X - pt.X) <= 6 && MathF.Abs(anchors[i].Y - pt.Y) <= 6)
                {
                    Checkpoint();
                    Selected = sel;
                    _drag = AnnotDragKind.ResizeShape;
                    _anchor = i;
                    _startBounds = sel.Bounds;
                    _startPt = pt;
                    return true;
                }
            }
        }

        // 3) 选中形状内部:选择模式 = 拖动;同类型工具 = 二次编辑重画
        if (Selected is { } s2 && s2.Bounds.Contains(pt.X, pt.Y))
        {
            if (Toolbar.ActiveTool is null)
            {
                Checkpoint();
                _drag = AnnotDragKind.MoveShape;
                _startBounds = s2.Bounds;
                _startPoints = s2.Points.ToArray();
                _startPt = pt;
                return true;
            }
            if ((int)Toolbar.ActiveTool.Value == (int)s2.Type)
            {
                Checkpoint();
                BeginDraft(pt, s2); // 重画该形状几何(保留 Id/色/粗细/序号)
                return true;
            }
        }

        // 4) 绘制工具激活 → 开始绘制
        if (Toolbar.ActiveTool is { } tool)
        {
            Checkpoint();
            BeginDraft(pt, null);
            if (tool == AnnotToolType.Number)
            {
                // 序号:按下即完成(圆心 = 点击点),无拖拽
                float r = MathF.Max(11f, 13f * _dpi);
                _draft!.Bounds = new PxRect((int)(pt.X - r), (int)(pt.Y - r), (int)(r * 2), (int)(r * 2));
                _draft.Number = Document.NextNumber();
                CommitDraft();
            }
            return true;
        }

        return false; // 交回 M1(选区移动/重选等)
    }

    /// <summary>移动。返回 true = 标注层在交互(调用方应重绘)。</summary>
    public bool HandleMouseMove(PxPoint pt)
    {
        switch (_drag)
        {
            case AnnotDragKind.Draw or AnnotDragKind.RedrawShape:
                UpdateDraft(pt);
                return true;
            case AnnotDragKind.MoveShape:
            {
                var s = Selected!;
                int dx = pt.X - _startPt.X, dy = pt.Y - _startPt.Y;
                s.Bounds = new PxRect(_startBounds.X + dx, _startBounds.Y + dy, _startBounds.Width, _startBounds.Height);
                for (int i = 0; i < s.Points.Count; i++)
                    s.Points[i] = new PxPoint(_startPoints[i].X + dx, _startPoints[i].Y + dy);
                return true;
            }
            case AnnotDragKind.ResizeShape:
            {
                var s = Selected!;
                s.Bounds = OverlayGeometry.AdjustFromAnchor(_startBounds, _anchor, pt);
                RescalePointsTo(_startBounds, s.Bounds, s);
                return true;
            }
            default:
                // 悬停高亮(仅工具条区域内引发重绘)
                if (Toolbar.Visible && Toolbar.Contains(pt) != Toolbar.Hover || Toolbar.Hover && Toolbar.HoverPt != pt)
                {
                    Toolbar.Hover = Toolbar.Contains(pt);
                    Toolbar.HoverPt = pt;
                    return Toolbar.Hover;
                }
                return false;
        }
    }

    /// <summary>抬起。返回 true = 已消费。</summary>
    public bool HandleMouseUp(PxPoint pt)
    {
        switch (_drag)
        {
            case AnnotDragKind.Draw or AnnotDragKind.RedrawShape:
                FinishDraftOrClick(pt);
                _drag = AnnotDragKind.None;
                return true;
            case AnnotDragKind.MoveShape or AnnotDragKind.ResizeShape:
                _drag = AnnotDragKind.None;
                _startPoints = Array.Empty<PxPoint>();
                return true;
        }
        return false;
    }

    /// <summary>滚轮:指针在工具条上 = 调粗细(选中形状则同步生效)。返回 true = 已消费。</summary>
    public bool HandleWheel(PxPoint pt, int delta)
    {
        if (!Toolbar.Visible || !Toolbar.Contains(pt)) return false;
        if (!Toolbar.WheelStroke(delta)) return true; // 吞掉但不变化
        if (Selected is { } s) { Checkpoint(); s.StrokeWidth = Toolbar.Stroke; }
        return true;
    }

    /// <summary>
    /// 键盘。vk 为虚拟键码;返回 true = 已消费。
    /// Del=删除选中;Ctrl+Z=撤销;Ctrl+Y=重做;Ctrl+Shift+Z=清空;空格=工具条显隐。
    /// </summary>
    public bool HandleKey(int vk, bool ctrl, bool shift)
    {
        switch (vk)
        {
            case 0x20: // VK_SPACE
                Toolbar.Visible = !Toolbar.Visible;
                return true;

            case 0x2E when Selected is not null: // VK_DELETE
                Checkpoint();
                Document.Shapes.Remove(Selected);
                Selected = null;
                return true;

            case 0x5A when ctrl: // 'Z'
                if (shift) { ClearAll(); return true; }     // Ctrl+Shift+Z = 清空(SPEC §9)
                if (!History.CanUndo) return true;          // 无可撤销也吞掉
                Document.Shapes = History.Undo(Document.Shapes);
                Selected = FindById(Selected);
                return true;

            case 0x59 when ctrl: // 'Y'
                if (!History.CanRedo) return true;
                Document.Shapes = History.Redo(Document.Shapes);
                Selected = FindById(Selected);
                return true;
        }
        return false;
    }

    /// <summary>清空全部标注(Ctrl+Shift+Z / 工具条清空按钮)。</summary>
    public void ClearAll()
    {
        if (Document.Shapes.Count == 0) return;
        Checkpoint();
        Document.Shapes.Clear();
        Selected = null;
        Document.ResetNumbers();
    }

    // —————————————————— 内部 ——————————————————

    private PxPoint[] _startPoints = Array.Empty<PxPoint>();

    private void Checkpoint() => History.Checkpoint(Document.Shapes);

    private AnnotShape? FindById(AnnotShape? s)
        => s is null ? null : Document.Shapes.Find(x => x.Id == s.Id);

    private void BeginDraft(PxPoint pt, AnnotShape? reedit)
    {
        _drag = reedit is null ? AnnotDragKind.Draw : AnnotDragKind.RedrawShape;
        _startPt = ClampToSelection(pt);
        _draft = reedit ?? new AnnotShape
        {
            Type = Toolbar.ActiveTool!.Value,
            ColorArgb = Toolbar.CurrentColorArgb,
            StrokeWidth = Toolbar.Stroke,
        };
        if (reedit is null)
        {
            _draft.Points.Add(_startPt);
        }
        else
        {
            _draft.Points.Clear(); // 二次编辑:几何重置,Id/样式保留
            _draft.Points.Add(_startPt);
        }
    }

    private void UpdateDraft(PxPoint ptRaw)
    {
        var s = _draft!;
        var pt = ClampToSelection(ptRaw);
        int dx = Math.Abs(pt.X - _startPt.X), dy = Math.Abs(pt.Y - _startPt.Y);

        switch (s.Type)
        {
            case AnnotToolType.Rectangle or AnnotToolType.Ellipse
                or AnnotToolType.Mosaic or AnnotToolType.Blur or AnnotToolType.Eraser:
                s.Bounds = OverlayGeometry.Normalize(_startPt, pt);
                break;

            case AnnotToolType.LineArrow:
                s.Points = new List<PxPoint> { _startPt, pt };
                s.Bounds = OverlayGeometry.Normalize(_startPt, pt);
                break;

            case AnnotToolType.Polyline or AnnotToolType.Pen or AnnotToolType.Highlighter:
                if (dx + dy > 2 || s.Points.Count == 1)
                {
                    s.Points.Add(pt);
                    s.Bounds = ExpandBounds(s.Bounds, pt);
                }
                break;
        }
    }

    private void FinishDraftOrClick(PxPoint pt)
    {
        var s = _draft;
        _draft = null;
        if (s is null) return;

        bool tinyMove = MathF.Abs(pt.X - _startPt.X) < ClickThresholdPx &&
                        MathF.Abs(pt.Y - _startPt.Y) < ClickThresholdPx;
        if (_drag == AnnotDragKind.RedrawShape)
        {
            // 二次编辑重画:形状已在文档中,几何已更新
            if (tinyMove) RestoreCheckpoint(); // 无效重画(单击):回滚
            return;
        }

        if (tinyMove)
        {
            // 单击:命中已有形状 → 选中并切回选择模式;否则丢弃空 draft
            var hit = Document.HitTest(pt);
            if (hit is not null)
            {
                Selected = hit;
                Toolbar.ActiveTool = null;
            }
            else
            {
                RestoreCheckpoint(); // 回滚(可能压了 checkpoint 但没画东西)
            }
            return;
        }

        CommitDraft();
        Selected = null; // 连续绘制模式:画完不自动选中(Snipaste 行为)
    }

    private void CommitDraft()
    {
        if (_draft is null) return;
        if (_draft.Type == AnnotToolType.Number && _draft.Number == 0)
            _draft.Number = Document.NextNumber();
        Document.Shapes.Add(_draft);
        _draft = null;
    }

    /// <summary>回滚最近一次 checkpoint(单击未产生有效操作时,撤销栈不留空记录)。</summary>
    private void RestoreCheckpoint()
    {
        if (!History.CanUndo) return;
        Document.Shapes = History.Undo(Document.Shapes); // 取出恢复
        // 取出的即"操作前"状态,重做栈不需要(等效无操作):清掉 Undo 时压入的 redo
        History.ResetRedo();
        Selected = FindById(Selected);
    }

    private void ExecuteToolbar(AnToolbarItem item)
    {
        switch (item.Action)
        {
            case AnToolbarAction.Tool:
                Toolbar.ActiveTool = item.Index == -1 ? null : (AnnotToolType)item.Index;
                if (Toolbar.ActiveTool is not null) Selected = null; // 进绘制工具取消选中
                break;
            case AnToolbarAction.Color:
                Toolbar.ColorIndex = item.Index;
                if (item.Index == 8) Toolbar.CustomCycleIndex++;
                if (Selected is { } sc)
                {
                    Checkpoint();
                    sc.ColorArgb = Toolbar.CurrentColorArgb;
                }
                break;
            case AnToolbarAction.Stroke:
                Toolbar.Stroke = item.Index;
                if (Selected is { } ss)
                {
                    Checkpoint();
                    ss.StrokeWidth = Toolbar.Stroke;
                }
                break;
            case AnToolbarAction.Undo:
                if (History.CanUndo)
                {
                    Document.Shapes = History.Undo(Document.Shapes);
                    Selected = FindById(Selected);
                }
                break;
            case AnToolbarAction.Redo:
                if (History.CanRedo)
                {
                    Document.Shapes = History.Redo(Document.Shapes);
                    Selected = FindById(Selected);
                }
                break;
            case AnToolbarAction.Clear:
                ClearAll();
                break;
            case AnToolbarAction.Confirm:
                ConfirmRequested?.Invoke();
                break;
            case AnToolbarAction.Cancel:
                CancelRequested?.Invoke();
                break;
        }
    }

    private PxPoint ClampToSelection(PxPoint pt)
    {
        int x = Math.Clamp(pt.X, _selection.X, Math.Max(_selection.X, _selection.X + _selection.Width - 1));
        int y = Math.Clamp(pt.Y, _selection.Y, Math.Max(_selection.Y, _selection.Y + _selection.Height - 1));
        return new PxPoint(x, y);
    }

    private static PxRect ExpandBounds(PxRect b, PxPoint p)
        => new(Math.Min(b.X, p.X), Math.Min(b.Y, p.Y),
               Math.Max(b.X + b.Width, p.X + 1) - Math.Min(b.X, p.X),
               Math.Max(b.Y + b.Height, p.Y + 1) - Math.Min(b.Y, p.Y));

    /// <summary>缩放后把点集按新旧外接矩形比例映射(折线/画笔/记号笔跟随缩放)。</summary>
    private static void RescalePointsTo(PxRect from, PxRect to, AnnotShape s)
    {
        if (s.Points.Count == 0 || from.Width <= 0 || from.Height <= 0) return;
        float kx = to.Width / (float)from.Width, ky = to.Height / (float)from.Height;
        for (int i = 0; i < s.Points.Count; i++)
        {
            s.Points[i] = new PxPoint(
                to.X + (int)MathF.Round((s.Points[i].X - from.X) * kx),
                to.Y + (int)MathF.Round((s.Points[i].Y - from.Y) * ky));
        }
    }
}
