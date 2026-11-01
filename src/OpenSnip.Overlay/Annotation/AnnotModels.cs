// M2a 标注引擎 —— 对象模型(SPEC §9:POCO、可 JSON 序列化)。
// 坐标系:虚拟桌面物理 px(与选区/会话一致);确认输出时平移到裁剪本地系合成。
using OpenSnip.Core;

namespace OpenSnip.Overlay.Annotation;

/// <summary>标注工具(SPEC §9 十工具;文本/放大镜/圆角矩形 = M3)。</summary>
public enum AnnotToolType
{
    Rectangle = 0,
    Ellipse,
    LineArrow,
    Polyline,
    Pen,
    Highlighter,
    Mosaic,
    Blur,
    Eraser,
    Number,
}

/// <summary>直线箭头样式(无/单/双)。</summary>
public enum AnnotArrow
{
    None = 0,
    Single,
    Double,
}

/// <summary>
/// 标注形状(POCO)。System.Text.Json 直接序列化
/// (PxRect/PxPoint 为位置 record struct,STJ 按构造参数名反序列化)。
/// </summary>
public sealed class AnnotShape
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public AnnotToolType Type { get; set; }

    /// <summary>外接矩形(虚拟桌面物理 px)。折线/画笔/记号笔 = 点集外接。</summary>
    public PxRect Bounds { get; set; }

    /// <summary>ARGB(Skia SKColor.ToArgb 语义;int 而非 SKColor,保持 POCO 无 Skia 依赖)。</summary>
    public int ColorArgb { get; set; } = unchecked((int)0xFFFF3B30);

    /// <summary>笔尖粗细(1-5)。</summary>
    public float StrokeWidth { get; set; } = 2f;

    public bool Filled { get; set; }

    public bool Dashed { get; set; }

    /// <summary>旋转(弧度;v1 恒 0,M3 预留)。</summary>
    public float Rotation { get; set; }

    /// <summary>文本内容(M3 文本工具预留)。</summary>
    public string? Text { get; set; }

    /// <summary>序号工具的编号(1-9 自动递增)。</summary>
    public int Number { get; set; }

    /// <summary>直线工具箭头样式。</summary>
    public AnnotArrow Arrow { get; set; } = AnnotArrow.Single;

    /// <summary>点集(折线/画笔/记号笔;其余工具为空)。</summary>
    public List<PxPoint> Points { get; set; } = new();

    public AnnotShape Clone()
    {
        var c = new AnnotShape
        {
            Id = Id,
            Type = Type,
            Bounds = Bounds,
            ColorArgb = ColorArgb,
            StrokeWidth = StrokeWidth,
            Filled = Filled,
            Dashed = Dashed,
            Rotation = Rotation,
            Text = Text,
            Number = Number,
            Arrow = Arrow,
            Points = new List<PxPoint>(Points),
        };
        return c;
    }
}

/// <summary>标注文档(SPEC §9 容器;POCO 可序列化)。</summary>
public sealed class AnnotDocument
{
    public List<AnnotShape> Shapes { get; set; } = new();

    private int _nextNumber = 1;

    /// <summary>序号 1-9 自动递增(超过 9 回绕到 1)。</summary>
    public int NextNumber()
    {
        int n = _nextNumber;
        _nextNumber = _nextNumber % 9 + 1;
        return n;
    }

    public void ResetNumbers() => _nextNumber = 1;

    /// <summary>
    /// 点命中(从最上层=列表尾往下);v1 用外接矩形外扩判定,
    /// 外扩量 = max(4px, 笔宽×2),直线/折线等细形也可稳定点中。
    /// </summary>
    public AnnotShape? HitTest(PxPoint pt)
    {
        for (int i = Shapes.Count - 1; i >= 0; i--)
        {
            var s = Shapes[i];
            int pad = (int)MathF.Max(4f, s.StrokeWidth * 2f);
            var b = s.Bounds;
            if (pt.X >= b.X - pad && pt.X < b.X + b.Width + pad &&
                pt.Y >= b.Y - pad && pt.Y < b.Y + b.Height + pad)
                return s;
        }
        return null;
    }

    public AnnotDocument CloneDocument()
    {
        var d = new AnnotDocument { Shapes = Shapes.ConvertAll(s => s.Clone()) };
        return d;
    }
}

/// <summary>
/// 撤销/重做栈(SPEC §9:深 100;快照式——操作前推入整个形状列表的深拷贝)。
/// 形状数量级小(&lt;100),整列表克隆微秒级,不在 <6ms 热路径成本内。
/// </summary>
public sealed class AnnotHistory
{
    private const int MaxDepth = 100;
    private readonly List<List<AnnotShape>> _undo = new();
    private readonly List<List<AnnotShape>> _redo = new();

    public static List<AnnotShape> CloneAll(IEnumerable<AnnotShape> src) => src.Select(s => s.Clone()).ToList();

    /// <summary>操作前调用:压入操作前状态快照,清空重做栈。</summary>
    public void Checkpoint(IReadOnlyList<AnnotShape> before)
    {
        _undo.Add(CloneAll(before));
        if (_undo.Count > MaxDepth) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>撤销:返回恢复到的状态(操作前快照),当前状态入重做栈。</summary>
    public List<AnnotShape> Undo(IReadOnlyList<AnnotShape> current)
    {
        if (_undo.Count == 0) return CloneAll(current);
        _redo.Add(CloneAll(current));
        if (_redo.Count > MaxDepth) _redo.RemoveAt(0);
        var restored = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return restored;
    }

    /// <summary>重做:返回恢复到的状态(操作后快照),当前状态入撤销栈。</summary>
    public List<AnnotShape> Redo(IReadOnlyList<AnnotShape> current)
    {
        if (_redo.Count == 0) return CloneAll(current);
        _undo.Add(CloneAll(current));
        if (_undo.Count > MaxDepth) _undo.RemoveAt(0);
        var restored = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return restored;
    }

    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>仅清空重做栈(等效无操作回滚时用)。</summary>
    public void ResetRedo() => _redo.Clear();
}
