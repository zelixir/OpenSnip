// Skia 绘制器(SPEC §3.2):
//   冻结画面 → 黑遮罩(选区内 0%/空闲全屏 50%/选区外加深)→ 悬停高亮(仅空闲)
//   → 选区边框/锚点/尺寸标签 → 十字准线(全屏 1px 半透明白)→ 放大镜(8x 最近邻 + HEX)。
// 性能要点:每帧全窗口写只有两遍(冻结 blit + Src 模式遮罩矩形),
// 遮罩用 SKBlendMode.Src 直接替换背衬内容,同时承担清屏职责,无独立 Clear 遍。
using OpenSnip.Core;
using OpenSnip.Overlay.Annotation;
using SkiaSharp;

namespace OpenSnip.Overlay;

/// <summary>单帧渲染模型(虚拟桌面物理坐标;绘制前换算为窗口局部坐标)。</summary>
internal readonly record struct RenderModel(
    PxPoint WindowOrigin,       // 本窗口左上角的虚拟桌面物理坐标
    int WindowWidth,
    int WindowHeight,
    SKImage? Frozen,            // 本屏冻结画面(该屏捕获失败时为 null)
    PxRect? Selection,          // 当前选区(虚拟桌面物理坐标)
    PxRect? HighlightWindow,    // 悬停高亮窗口矩形(虚拟桌面物理坐标;非空闲阶段为 null)
    PxPoint? CursorPt,          // 光标虚拟物理坐标(未知时 null)
    string? MagnifierHex,       // 放大镜中心像素 "#RRGGBB"(不可得时 null)
    bool ShowMagnifier,
    float DpiScale,
    System.Collections.Generic.IReadOnlyList<AnnotShape>? Shapes = null, // §9 标注(空文档传空表即可)
    AnnotShape? SelectedShape = null,                                    // 选中态(高亮框+8 锚点)
    AnnotToolbar? Toolbar = null,                                        // 工具条(Visible 由对象决定)
    AnnotShape? DraftShape = null,                                       // §14 编辑中的文本草稿
    bool TextEditing = false,                                            // §14 文本编辑态(光标闪烁)
    PxRect? ElementHighlight = null,                                     // §13 元素悬停青框
    bool DetectingHint = false,                                          // §13 "检测中"提示
    bool Whiteboard = false);                                            // §15 白板:无遮罩/无选区 UI/无准线

/// <summary>绘制器本身无状态(画笔缓存在实例上,仅 Overlay UI 线程使用)。</summary>
internal sealed class OverlayRenderer
{
    // —— SPEC §3.2 配色/尺寸 ——
    private const float MagnifierZoom = 8f;      // 放大 8 倍(最近邻)
    private const float MagnifierSizePx = 160f;  // 放大镜边长(逻辑 px)
    private const float MagnifierGapPx = 20f;    // 与光标间距(逻辑 px)

    // 选区外最终暗度 = 1-(1-50%)*(1-39%) ≈ 69.6%(50% 全屏遮罩 + 选区外加深,合成等效一次写)
    private const byte OutsideAlpha = 177;

    private SKPaint? _paintFrozen;
    private SKPaint? _paintMaskSrc;              // 空闲全屏 50% 黑(Src 替换写)
    private SKPaint? _paintDarkenSrc;            // 选区外加深(Src 替换写)
    private SKPaint? _paintHighlightFill;
    private SKPaint? _paintHighlightBorder;      // 2px 蓝 #00A8FF
    private SKPaint? _paintSelectionBorder;      // 1px 红
    private SKPaint? _paintAnchorFill;
    private SKPaint? _paintAnchorBorder;
    private SKPaint? _paintCrosshair;            // 1px 半透明白
    private SKPaint? _paintLabelBg;
    private SKPaint? _paintLabelText;
    private SKPaint? _paintMagBg;
    private SKPaint? _paintMagHexBg;
    private SKPaint? _paintMagBorder;
    private SKTypeface? _typeface;

    private SKPaint P(ref SKPaint? field, Func<SKPaint> make) => field ??= make();

    private SKPaint FrozenPaint => P(ref _paintFrozen, () => new SKPaint { IsAntialias = false });
    private SKPaint MaskPaint => P(ref _paintMaskSrc, () => new SKPaint
    {
        // SPEC:50% 黑遮罩。必须 SrcOver(alpha 混合保留底下冻结画面)!
        // 曾误用 Src 整像素替换 → DIB 全被替换成纯黑遮罩色,渲染输出全暗(E2E 20261031-141858)。
        Color = new SKColor(0, 0, 0, 128),
        IsAntialias = false,
    });
    private SKPaint DarkenPaint => P(ref _paintDarkenSrc, () => new SKPaint
    {
        // 选区外加深:同上,SrcOver 叠加(177 = 50% 全屏遮罩 + 39% 加深的合成等效单层)
        Color = new SKColor(0, 0, 0, OutsideAlpha),
        IsAntialias = false,
    });
    private SKPaint HighlightFillPaint => P(ref _paintHighlightFill, () => new SKPaint { Color = new SKColor(0x00, 0xA8, 0xFF, 40) });
    private SKPaint HighlightBorderPaint => P(ref _paintHighlightBorder, () => new SKPaint
    {
        Color = new SKColor(0x00, 0xA8, 0xFF),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 2f,
        IsAntialias = false,
    });
    private SKPaint SelectionBorderPaint => P(ref _paintSelectionBorder, () => new SKPaint
    {
        Color = SKColors.Red,                    // SPEC:边框 1px 红 #FF0000
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f,
        IsAntialias = false,
    });
    private SKPaint AnchorFillPaint => P(ref _paintAnchorFill, () => new SKPaint { Color = SKColors.White, IsAntialias = false });
    private SKPaint AnchorBorderPaint => P(ref _paintAnchorBorder, () => new SKPaint
    {
        Color = new SKColor(0, 0, 0, 160),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f,
        IsAntialias = false,
    });
    private SKPaint CrosshairPaint => P(ref _paintCrosshair, () => new SKPaint
    {
        Color = new SKColor(255, 255, 255, 100), // 半透明白
        StrokeWidth = 1f,
        IsAntialias = false,
    });
    private SKPaint LabelBgPaint => P(ref _paintLabelBg, () => new SKPaint { Color = new SKColor(0, 0, 0, 210) });
    private SKPaint LabelTextPaint => P(ref _paintLabelText, () => new SKPaint { Color = SKColors.White, IsAntialias = true });
    private SKPaint MagBgPaint => P(ref _paintMagBg, () => new SKPaint { Color = SKColors.White });
    private SKPaint MagHexBgPaint => P(ref _paintMagHexBg, () => new SKPaint { Color = new SKColor(0, 0, 0, 210) });
    private SKPaint MagBorderPaint => P(ref _paintMagBorder, () => new SKPaint
    {
        Color = new SKColor(255, 255, 255, 220),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f,
        IsAntialias = false,
    });

    private SKTypeface Typeface => _typeface ??= SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;

    /// <summary>
    /// 绘制一帧。canvas 为窗口背衬。
    /// 清屏策略:冻结画面 blit(源全不透明)本身即整窗替换,无需独立 Clear 遍;
    /// 仅捕获失败(无冻结画面)时 Clear 兜底,防止旧帧残留(放大镜拖尾等)。
    /// </summary>
    public void DrawFrame(SKCanvas canvas, in RenderModel m)
    {
        var bounds = new SKRect(0, 0, m.WindowWidth, m.WindowHeight);

        // 1) 冻结画面 1:1 blit(源经 EnsureOpaque 保证全不透明 → SrcOver 即整窗替换 = 隐式清屏)
        if (m.Frozen is not null)
        {
            canvas.DrawImage(m.Frozen, bounds, bounds, SKSamplingOptions.Default, FrozenPaint); // Default=最近邻,1:1 blit
        }
        else
        {
            canvas.Clear(SKColors.Transparent); // 无画面兜底:罕见路径,不追求零成本
        }

        // 1.5) 标注(SPEC §9:画于冻结画面之上、遮罩之下;标注限定选区内,选区内无遮罩)
        if (m.Shapes is { Count: > 0 } || m.DraftShape is not null)
            AnnotationRenderer.Draw(canvas, m.Shapes ?? Array.Empty<AnnotShape>(), m.Frozen, m.SelectedShape,
                m.WindowOrigin, m.DpiScale, m.DraftShape, m.TextEditing);

        // 1.6) §13 元素悬停青框(Hover 相位,窗口蓝框不并存)
        if (m.ElementHighlight is { } eh)
            AnnotationRenderer.DrawElementHighlight(canvas, eh, m.WindowOrigin);

        // 2) 遮罩 + 选区 UI(白板模式全部跳过:纯画面 + 标注,§15)
        if (m.Whiteboard)
        {
            // 白板:直接进入选区元素之外的 UI(工具条/文本光标已在标注层),无遮罩
        }
        else if (m.Selection is { } sel)
        {
            var local = ToLocal(m, sel);
            float L = local.Left, T = local.Top, R = local.Right, B = local.Bottom;
            float W = m.WindowWidth, H = m.WindowHeight;
            FillDarken(canvas, 0, 0, W, T);
            FillDarken(canvas, 0, B, W, H - B);
            FillDarken(canvas, 0, T, L, B - T);
            FillDarken(canvas, R, T, W - R, B - T);

            // 3) 选区元素:边框 1px 红 / 8 锚点白方块 / 右上尺寸标签
            canvas.DrawRect(local, SelectionBorderPaint);
            DrawAnchors(canvas, m, sel);
            DrawSizeLabel(canvas, m, sel, local);
        }
        else
        {
            canvas.DrawRect(bounds, MaskPaint);

            // 窗口悬停高亮(仅空闲;拖拽后消失,SPEC §3.2)
            if (m.HighlightWindow is { } hl)
                DrawHighlight(canvas, m, hl);
        }

        // 4) 十字准线(全屏 1px 半透明白;标注工具条可见时抑制,避免穿过标注;白板不画)
        bool annotateMode = m.Toolbar is { Visible: true };
        if (!annotateMode && !m.Whiteboard && m.CursorPt is { } cur)
            DrawCrosshair(canvas, m, cur);

        // 5) 放大镜(拖拽中隐藏;标注模式不画,工具条在附近;白板不画)
        if (!annotateMode && !m.Whiteboard && m.ShowMagnifier && m.CursorPt is { } cur2)
            DrawMagnifier(canvas, m, cur2);

        // 6) 工具条(最顶 UI,§9)
        if (annotateMode)
            AnnotationRenderer.DrawToolbar(canvas, m.Toolbar!, m.WindowOrigin, m.CursorPt, m.DpiScale);

        // 7) §13 "检测中"提示(空响应软处理)
        if (m.DetectingHint)
            AnnotationRenderer.DrawDetectingHint(canvas, m.WindowWidth, m.WindowHeight, m.DpiScale);

        canvas.Flush();
    }

    private static SKRect ToLocal(in RenderModel m, PxRect r)
        => new(
            r.X - m.WindowOrigin.X,
            r.Y - m.WindowOrigin.Y,
            r.X + r.Width - m.WindowOrigin.X,
            r.Y + r.Height - m.WindowOrigin.Y);

    private void DrawHighlight(SKCanvas canvas, in RenderModel m, PxRect hl)
    {
        var local = ToLocal(m, hl);
        canvas.DrawRect(local, HighlightFillPaint);
        canvas.DrawRect(local, HighlightBorderPaint);
    }

    private void FillDarken(SKCanvas canvas, float x, float y, float w, float h)
    {
        if (w <= 0 || h <= 0) return;
        canvas.DrawRect(new SKRect(x, y, x + w, y + h), DarkenPaint);
    }

    private void DrawAnchors(SKCanvas canvas, in RenderModel m, PxRect sel)
    {
        float a = MathF.Max(6f, MathF.Round(8f * MathF.Max(1f, m.DpiScale))); // 锚点方块边长
        foreach (var c in OverlayGeometry.AnchorCenters(sel))
        {
            float cx = c.X - m.WindowOrigin.X;
            float cy = c.Y - m.WindowOrigin.Y;
            var r = new SKRect(cx - a / 2f, cy - a / 2f, cx + a / 2f, cy + a / 2f);
            canvas.DrawRect(r, AnchorFillPaint);
            canvas.DrawRect(r, AnchorBorderPaint);
        }
    }

    private void DrawSizeLabel(SKCanvas canvas, in RenderModel m, PxRect sel, SKRect local)
    {
        // SPEC:右上角尺寸标签 "W×H (px)"
        string text = $"{sel.Width}×{sel.Height} (px)";
        float dpi = MathF.Max(1f, m.DpiScale);
        float fontSize = MathF.Max(11f, 12f * dpi);
        using var font = new SKFont(Typeface, fontSize);
        float tw = font.MeasureText(text);
        float th = fontSize * 1.35f;
        float pad = 4f * dpi;

        // 优先贴选区右上角外侧;顶部越界则贴内侧;右缘越界贴窗口右缘
        float x = local.Right - tw - pad * 2;
        float y = local.Top - th;
        if (y < 0) y = local.Top;
        if (x < 0) x = MathF.Max(0, m.WindowWidth - tw - pad * 2);

        var bg = new SKRect(x, y, x + tw + pad * 2, y + th);
        canvas.DrawRect(bg, LabelBgPaint);
        canvas.DrawText(text, new SKPoint(x + pad, y + th - pad * 0.75f), SKTextAlign.Left, font, LabelTextPaint);
    }

    private void DrawCrosshair(SKCanvas canvas, in RenderModel m, PxPoint cur)
    {
        float cx = cur.X - m.WindowOrigin.X;
        float cy = cur.Y - m.WindowOrigin.Y;
        canvas.DrawLine(0, cy, m.WindowWidth, cy, CrosshairPaint);
        canvas.DrawLine(cx, 0, cx, m.WindowHeight, CrosshairPaint);
    }

    private void DrawMagnifier(SKCanvas canvas, in RenderModel m, PxPoint cursor)
    {
        float dpi = MathF.Max(1f, m.DpiScale);
        float size = MagnifierSizePx * dpi;
        float gap = MagnifierGapPx * dpi;
        float cx = cursor.X - m.WindowOrigin.X;
        float cy = cursor.Y - m.WindowOrigin.Y;

        // SPEC 字面:置于光标左下;贴左/下缘时翻到另一侧
        float x = cx - size - gap;
        if (x < 0) x = cx + gap;
        float y = cy + gap;
        if (y + size > m.WindowHeight) y = cy - size - gap;
        x = Math.Clamp(x, 0, MathF.Max(0, m.WindowWidth - size));
        y = Math.Clamp(y, 0, MathF.Max(0, m.WindowHeight - size));

        var box = new SKRect(x, y, x + size, y + size);
        float stripH = 20f * dpi; // HEX 色值条(SPEC:下方显示 HEX)

        float srcSpan = size / MagnifierZoom; // 源区域边长(物理 px)
        float sx = Math.Clamp(cx - srcSpan / 2f, 0, MathF.Max(0, m.WindowWidth - srcSpan));
        float sy = Math.Clamp(cy - srcSpan / 2f, 0, MathF.Max(0, m.WindowHeight - srcSpan));

        canvas.Save();
        canvas.ClipRect(box);
        canvas.DrawRect(box, MagBgPaint); // 越界采样兜底白底
        if (m.Frozen is not null)
        {
            canvas.Save();
            // 平移+缩放后整图绘出,ClipRect 保证只栅格化放大镜区域(SKSamplingOptions.Default=最近邻)
            canvas.Translate(x - sx * MagnifierZoom, y - sy * MagnifierZoom);
            canvas.Scale(MagnifierZoom, MagnifierZoom);
            var full = new SKRect(0, 0, m.Frozen.Width, m.Frozen.Height);
            canvas.DrawImage(m.Frozen, full, full, SKSamplingOptions.Default, FrozenPaint); // 8x 放大必须最近邻
            canvas.Restore();
        }

        // HEX 色值条
        var strip = new SKRect(x, y + size - stripH, x + size, y + size);
        canvas.DrawRect(strip, MagHexBgPaint);
        if (m.MagnifierHex is { } hex)
        {
            float fs = MathF.Max(10f, 11f * dpi);
            using var font = new SKFont(Typeface, fs);
            canvas.DrawText(hex, new SKPoint(x + size / 2f, strip.MidY + fs * 0.35f), SKTextAlign.Center, font, LabelTextPaint);
        }
        canvas.Restore();

        canvas.DrawRect(box, MagBorderPaint);
    }
}
