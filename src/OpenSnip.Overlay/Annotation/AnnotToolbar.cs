// M2a 工具条(SPEC §9):空格显隐;工具按钮×10(另含"选择"槽)/颜色板 8 色+自定义/
// 粗细 1-5/撤销/重做/清空/确认/取消;滚轮=粗细(指针在工具条上时)。
// 两行固定布局(按可用宽度自适应单行/两行),Skia 绘制在 AnnotationRenderer.DrawToolbar。
using OpenSnip.Core;

namespace OpenSnip.Overlay.Annotation;

/// <summary>工具条命中动作种类。</summary>
internal enum AnToolbarAction
{
    None = 0,
    Tool,       // index: AnnotToolType(-1 = 选择槽)
    Color,      // index: 0-7 固定色;8 = 自定义(循环扩展色)
    Stroke,     // index: 1-5
    Undo,
    Redo,
    Clear,
    Confirm,
    Cancel,
}

/// <summary>工具条单个可命中元素。</summary>
internal readonly record struct AnToolbarItem(AnToolbarAction Action, int Index, PxRect Rect);

/// <summary>
/// 工具条状态与布局(纯数据+几何;绘制在 AnnotationRenderer)。
/// 仅 Overlay UI 线程触碰。
/// </summary>
internal sealed class AnnotToolbar
{
    /// <summary>固定 8 色(Snipaste 风:红橙黄绿青蓝紫黑)+ 扩展自定义循环色。</summary>
    public static readonly int[] Palette =
    {
        unchecked((int)0xFFFF3B30), // 红
        unchecked((int)0xFFFF9500), // 橙
        unchecked((int)0xFFFFDD00), // 黄
        unchecked((int)0xFF34C759), // 绿
        unchecked((int)0xFF32ADE6), // 青
        unchecked((int)0xFF007AFF), // 蓝
        unchecked((int)0xFFAF52DE), // 紫
        unchecked((int)0xFF1C1C1E), // 黑
    };

    /// <summary>自定义槽循环色(每点一次换一色)。</summary>
    public static readonly int[] CustomCycle =
    {
        unchecked((int)0xFFFF2D55),
        unchecked((int)0xFF5AC8FA),
        unchecked((int)0xFF8E8E93),
        unchecked((int)0xFFFFFFFF),
        unchecked((int)0xFFA2845E),
    };

    public bool Visible = true;                 // 空格切换;Selected 相位默认显示
    public bool Hover;                          // 指针悬停在工具条上(高亮)
    public PxPoint? HoverPt;                    // 悬停位置(重绘用)

    /// <summary>当前激活工具;null = 选择模式(单击选中/拖动/缩放)。</summary>
    public AnnotToolType? ActiveTool;

    public int ColorIndex;                      // 0-7;8 = 自定义
    public int CustomCycleIndex;
    public int Stroke = 2;                      // 1-5

    public PxRect FrameRect;                    // 工具条整体外框(窗口本地系由调用方换算;此处存布局时给定的系)
    public List<AnToolbarItem> Items { get; } = new();

    /// <summary>当前生效颜色 ARGB(考虑自定义循环)。</summary>
    public int CurrentColorArgb => ColorIndex switch
    {
        >= 0 and < 8 => Palette[ColorIndex],
        _ => CustomCycle[CustomCycleIndex % CustomCycle.Length],
    };

    /// <summary>
    /// 重新布局。selection 为选区(与 rect 同一坐标系),窗口宽高用于越界回退。
    /// 位置:选区上方优先;越界放选区内顶部;x 靠左,超宽靠右。
    /// </summary>
    public void Layout(PxRect selection, int windowWidth, int windowHeight, float dpi)
    {
        Items.Clear();
        float unit = MathF.Max(24f, 28f * dpi);   // 槽位边长
        float pad = 4f * dpi;                     // 外框内边距
        float gap = 2f * dpi;

        // 两行:行1 = 选择+10 工具+撤销/重做/清空/确认/取消(16 槽);行2 = 8 色+自定义+5 粗细(14 槽)
        int row1 = 17; // 两行布局:行1 = 选择+11 工具(§14 文本)+5 动作;行2 = 8 色+自定义+5 粗细
        float rowW = row1 * (unit + gap) - gap + pad * 2;
        float rowH = unit;
        float totalH = pad * 2 + rowH * 2 + gap;

        float x = selection.X;
        float y = selection.Y - totalH - 4f * dpi;
        if (y < 0) y = selection.Y + 4f * dpi;                       // 顶部越界 → 选区内
        if (x + rowW > windowWidth) x = MathF.Max(0, windowWidth - rowW - 4f * dpi);

        FrameRect = new PxRect((int)x, (int)y, (int)rowW, (int)totalH);

        float ix = x + pad;
        float iy = y + pad;

        void AddItem(AnToolbarAction a, int idx)
        {
            Items.Add(new AnToolbarItem(a, idx,
                new PxRect((int)ix, (int)iy, (int)unit, (int)unit)));
            ix += unit + gap;
        }

        // 行 1:选择槽(-1)+ 10 工具
        AddItem(AnToolbarAction.Tool, -1);
        for (int t = 0; t < 11; t++) AddItem(AnToolbarAction.Tool, t);
        // 行 1:撤销/重做/清空/确认/取消
        AddItem(AnToolbarAction.Undo, 0);
        AddItem(AnToolbarAction.Redo, 0);
        AddItem(AnToolbarAction.Clear, 0);
        AddItem(AnToolbarAction.Confirm, 0);
        AddItem(AnToolbarAction.Cancel, 0);

        // 行 2
        ix = x + pad;
        iy += rowH + gap;
        for (int ci = 0; ci < 8; ci++) AddItem(AnToolbarAction.Color, ci);
        AddItem(AnToolbarAction.Color, 8);                            // 自定义循环
        for (int s = 1; s <= 5; s++) AddItem(AnToolbarAction.Stroke, s);
    }

    /// <summary>点命中(相对与布局同一坐标系)。</summary>
    public AnToolbarItem HitTest(PxPoint pt)
    {
        foreach (var it in Items)
        {
            if (pt.X >= it.Rect.X && pt.X < it.Rect.X + it.Rect.Width &&
                pt.Y >= it.Rect.Y && pt.Y < it.Rect.Y + it.Rect.Height)
                return it;
        }
        return new AnToolbarItem(AnToolbarAction.None, 0, PxRect.Empty);
    }

    public bool Contains(PxPoint pt)
        => pt.X >= FrameRect.X && pt.X < FrameRect.X + FrameRect.Width &&
           pt.Y >= FrameRect.Y && pt.Y < FrameRect.Y + FrameRect.Height;

    /// <summary>滚轮调粗细(指针在工具条上);返回 true 表示粗细变化。</summary>
    public bool WheelStroke(int delta)
    {
        int ns = Math.Clamp(Stroke + (delta > 0 ? 1 : -1), 1, 5);
        if (ns == Stroke) return false;
        Stroke = ns;
        return true;
    }
}
