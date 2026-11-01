// M2a 标注渲染器(SPEC §9):Skia,画于冻结画面之上、遮罩之下。
//   马赛克 = 区域 16px 块降采样(直读冻结帧 premul 指针;冻结帧不透明 → premul 值 == straight 值);
//   模糊   = SKImageFilter.CreateBlur 区域裁剪;
//   橡皮擦 = 裁剪区域内从冻结帧重 blit 恢复原像素。
// 坐标:形状为虚拟桌面物理 px;canvas 先 Translate(-origin) 统一到桌面系再绘制。
using OpenSnip.Core;
using SkiaSharp;

namespace OpenSnip.Overlay.Annotation;

internal static class AnnotationRenderer
{
    private const float MosaicBlock = 16f;
    private const float BlurSigma = 8f;

    private static readonly SKTypeface UiTypeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;

    /// <summary>
    /// 文本度量(§14:ReflowText 增长文本框用)。多行以 \n 分隔;返回 (宽, 高) 逻辑 px(fontSize 已含 DPI)。
    /// </summary>
    public static (float Width, float Height) MeasureText(string text, float fontSize)
    {
        using var font = new SKFont(UiTypeface, fontSize);
        float maxW = 0;
        int lines = 1;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0) maxW = MathF.Max(maxW, font.MeasureText(line));
            lines++;
        }
        var m = font.Metrics;
        float lineH = m.Descent - m.Ascent;
        return (maxW, lineH * lines);
    }

    /// <summary>绘制全部标注 + 选中态。shapes 为桌面坐标;frozen 为本屏冻结画面(窗口本地原点 origin)。</summary>
    public static void Draw(SKCanvas canvas, IReadOnlyList<AnnotShape> shapes, SKImage? frozen,
        AnnotShape? selected, PxPoint windowOrigin, float dpi,
        AnnotShape? draft = null, bool textEditing = false)
    {
        canvas.Save();
        canvas.Translate(-windowOrigin.X, -windowOrigin.Y);
        foreach (var s in shapes)
            DrawShape(canvas, s, frozen, windowOrigin, dpi);

        // §14 编辑中的文本草稿(未入文档):文本 + 闪烁光标 + 虚线编辑框
        if (draft is { Type: AnnotToolType.Text } te)
        {
            DrawShape(canvas, te, frozen, windowOrigin, dpi);
            if (textEditing)
                DrawTextEditingChrome(canvas, te, dpi);
        }

        if (selected is { } sel)
            DrawSelectionChrome(canvas, sel, dpi);
        canvas.Restore();
    }

    /// <summary>文本编辑态提示:虚线框 + 闪烁光标(500ms 周期)。</summary>
    private static void DrawTextEditingChrome(SKCanvas canvas, AnnotShape te, float dpi)
    {
        var b = te.Bounds;
        using var dash = new SKPaint
        {
            Color = new SKColor(0x32, 0xAD, 0xE6),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = false,
            PathEffect = SKPathEffect.CreateDash(new float[] { 4f, 3f }, 0f),
        };
        canvas.DrawRect(new SKRect(b.X - 3, b.Y - 3, b.X + b.Width + 3, b.Y + b.Height + 3), dash);

        // 光标:末行末尾竖线,500ms 周期闪烁
        if (DateTime.Now.Millisecond < 250) return;
        float fontSize = AnnotShape.FontSizeFromStroke(te.StrokeWidth) * MathF.Max(1f, dpi);
        var lines = (te.Text ?? string.Empty).Split('\n');
        string last = lines[^1];
        using var font = new SKFont(UiTypeface, fontSize);
        float w = last.Length > 0 ? font.MeasureText(last) : 0;
        var m = font.Metrics;
        float lineH = m.Descent - m.Ascent;
        float cx = b.X + w + 1f;
        float cyTop = b.Y + lineH * (lines.Length - 1) + m.Ascent;
        using var caret = new SKPaint { Color = new SKColor(0x32, 0xAD, 0xE6), StrokeWidth = MathF.Max(1f, 1.5f * dpi), IsAntialias = false };
        canvas.DrawLine(cx, cyTop, cx, cyTop + lineH, caret);
    }

    private static SKColor ToColor(int argb)
        => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    private static void DrawShape(SKCanvas canvas, AnnotShape s, SKImage? frozen, PxPoint origin, float dpi)
    {
        float w = MathF.Max(1f, s.StrokeWidth * dpi);
        using var stroke = new SKPaint
        {
            Color = ToColor(s.ColorArgb),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = w,
            IsAntialias = true,
        };
        if (s.Dashed)
            stroke.PathEffect = SKPathEffect.CreateDash(new float[] { w * 3f, w * 2.4f }, 0f);
        using var fill = new SKPaint { Color = ToColor(s.ColorArgb), IsAntialias = true };

        var b = s.Bounds;
        switch (s.Type)
        {
            case AnnotToolType.Rectangle:
                if (s.Filled) canvas.DrawRect(ToRect(b), fill);
                canvas.DrawRect(ToRect(b), stroke);
                break;

            case AnnotToolType.Ellipse:
                if (s.Filled) canvas.DrawOval(ToRect(b), fill);
                canvas.DrawOval(ToRect(b), stroke);
                break;

            case AnnotToolType.LineArrow:
            {
                var (p0, p1) = Endpoints(s);
                canvas.DrawLine(p0, p1, stroke);
                float head = MathF.Max(10f, w * 4f);
                if (s.Arrow is AnnotArrow.Single or AnnotArrow.Double)
                    DrawArrowHead(canvas, p1, p0, head, stroke);
                if (s.Arrow is AnnotArrow.Double)
                    DrawArrowHead(canvas, p0, p1, head, stroke);
                break;
            }

            case AnnotToolType.Polyline:
            {
                if (s.Points.Count < 2) break;
                var pb = new SKPathBuilder();
                pb.MoveTo(ToPoint(s.Points[0]));
                for (int i = 1; i < s.Points.Count; i++) pb.LineTo(ToPoint(s.Points[i]));
                using var path = pb.Snapshot();
                canvas.DrawPath(path, stroke);
                break;
            }

            case AnnotToolType.Pen:
            {
                if (s.Points.Count < 2) break;
                var pb = new SKPathBuilder();
                pb.MoveTo(ToPoint(s.Points[0]));
                for (int i = 1; i < s.Points.Count; i++) pb.LineTo(ToPoint(s.Points[i]));
                using var path = pb.Snapshot();
                stroke.StrokeCap = SKStrokeCap.Round;
                canvas.DrawPath(path, stroke);
                break;
            }

            case AnnotToolType.Highlighter:
            {
                // 记号笔:半透明粗线(方形笔触),叠在画面上
                using var hl = new SKPaint
                {
                    Color = new SKColor(ToColor(s.ColorArgb).Red, ToColor(s.ColorArgb).Green, ToColor(s.ColorArgb).Blue, 90),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = MathF.Max(8f, w * 5f),
                    StrokeCap = SKStrokeCap.Butt,
                    IsAntialias = false,
                };
                if (s.Points.Count < 2)
                {
                    var (p0, p1) = Endpoints(s);
                    canvas.DrawLine(p0, p1, hl);
                }
                else
                {
                    var pb = new SKPathBuilder();
                    pb.MoveTo(ToPoint(s.Points[0]));
                    for (int i = 1; i < s.Points.Count; i++) pb.LineTo(ToPoint(s.Points[i]));
                    using var path = pb.Snapshot();
                    canvas.DrawPath(path, hl);
                }
                break;
            }

            case AnnotToolType.Mosaic:
                DrawMosaic(canvas, frozen, b, origin);
                break;

            case AnnotToolType.Blur:
                DrawBlur(canvas, frozen, b, origin);
                break;

            case AnnotToolType.Eraser:
                // 橡皮擦:从冻结帧重 blit 恢复原像素
                if (frozen is not null)
                {
                    canvas.Save();
                    canvas.ClipRect(ToRect(b));
                    canvas.DrawImage(frozen,
                        new SKRect(origin.X, origin.Y, origin.X + frozen.Width, origin.Y + frozen.Height),
                        SKSamplingOptions.Default);
                    canvas.Restore();
                }
                break;

            case AnnotToolType.Text:
            {
                // §14 文本:多行,字号 = 粗细档映射 ×DPI;编辑态由 DrawTextEditingChrome 补光标
                float fontSize = AnnotShape.FontSizeFromStroke(s.StrokeWidth) * MathF.Max(1f, dpi);
                using var font = new SKFont(UiTypeface, fontSize);
                var metrics = font.Metrics;
                float lineH = metrics.Descent - metrics.Ascent;
                var lines = (s.Text ?? string.Empty).Split('\n');
                float y = b.Y - metrics.Ascent;
                using var textPaint = new SKPaint { Color = ToColor(s.ColorArgb), IsAntialias = true };
                foreach (var line in lines)
                {
                    if (line.Length > 0)
                        canvas.DrawText(line, b.X, y, SKTextAlign.Left, font, textPaint);
                    y += lineH;
                }
                break;
            }

            case AnnotToolType.Number:
            {
                float r = MathF.Max(11f, 13f * dpi);
                var c = new SKPoint(b.X + b.Width / 2f, b.Y + b.Height / 2f);
                canvas.DrawCircle(c, r, fill);
                using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, r * 1.35f);
                canvas.DrawText(s.Number.ToString(), c.X, c.Y + r * 0.47f, SKTextAlign.Center, font,
                    new SKPaint { Color = SKColors.White, IsAntialias = true });
                break;
            }
        }
    }

    private static SKRect ToRect(PxRect r) => new(r.X, r.Y, r.X + r.Width, r.Y + r.Height);

    private static SKPoint ToPoint(PxPoint p) => new(p.X, p.Y);

    /// <summary>直线/未拖拽完的点集形状的两端点。</summary>
    private static (SKPoint a, SKPoint b) Endpoints(AnnotShape s)
    {
        if (s.Points.Count >= 2) return (ToPoint(s.Points[0]), ToPoint(s.Points[^1]));
        var b = s.Bounds;
        return (new SKPoint(b.X, b.Y), new SKPoint(b.X + b.Width, b.Y + b.Height));
    }

    private static void DrawArrowHead(SKCanvas canvas, SKPoint tip, SKPoint from, float size, SKPaint p)
    {
        float ang = MathF.Atan2(tip.Y - from.Y, tip.X - from.X);
        for (int s = -1; s <= 1; s += 2)
        {
            var wing = new SKPoint(
                tip.X - size * MathF.Cos(ang + s * 0.45f),
                tip.Y - size * MathF.Sin(ang + s * 0.45f));
            canvas.DrawLine(tip, wing, p);
        }
    }

    /// <summary>马赛克:16px 块(桌面绝对网格,移动时块稳定)均值色块。</summary>
    private static unsafe void DrawMosaic(SKCanvas canvas, SKImage? frozen, PxRect r, PxPoint origin)
    {
        if (frozen is null)
        {
            using var fallback = new SKPaint { Color = new SKColor(128, 128, 128, 200) };
            canvas.DrawRect(ToRect(r), fallback);
            return;
        }
        var pix = frozen.PeekPixels();
        if (pix is null) return;
        byte* basePtr = (byte*)pix.GetPixels();
        int stride = pix.RowBytes;
        int fw = frozen.Width, fh = frozen.Height;
        const int block = (int)MosaicBlock;

        int x0 = Math.Max(r.X, origin.X), x1 = Math.Min(r.X + r.Width, origin.X + fw);
        int y0 = Math.Max(r.Y, origin.Y), y1 = Math.Min(r.Y + r.Height, origin.Y + fh);
        if (x1 <= x0 || y1 <= y0) return;

        using var paint = new SKPaint { IsAntialias = false };
        for (int by = y0 / block * block; by < y1; by += block)
        {
            for (int bx = x0 / block * block; bx < x1; bx += block)
            {
                int sx = Math.Max(bx, x0), sy = Math.Max(by, y0);
                int ex = Math.Min(bx + block, x1), ey = Math.Min(by + block, y1);
                if (ex <= sx || ey <= sy) continue;
                long sb = 0, sg = 0, sr = 0; int n = 0;
                for (int y = sy; y < ey; y++)
                {
                    byte* row = basePtr + (long)(y - origin.Y) * stride;
                    for (int x = sx; x < ex; x++)
                    {
                        byte* p = row + (long)(x - origin.X) * 4;
                        sb += p[0]; sg += p[1]; sr += p[2]; n++;
                    }
                }
                if (n == 0) continue;
                paint.Color = new SKColor((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
                canvas.DrawRect(bx, by, block, block, paint);
            }
        }
    }

    /// <summary>模糊:区域裁剪 + 高斯 ImageFilter 重绘冻结帧。</summary>
    private static void DrawBlur(SKCanvas canvas, SKImage? frozen, PxRect r, PxPoint origin)
    {
        if (frozen is null)
        {
            using var fallback = new SKPaint { Color = new SKColor(160, 160, 160, 200) };
            canvas.DrawRect(ToRect(r), fallback);
            return;
        }
        using var paint = new SKPaint();
        paint.ImageFilter = SKImageFilter.CreateBlur(BlurSigma, BlurSigma);
        // 性能关键:ImageFilter 若对整图滤波(2K≈8.8M 像素/帧)会突破 <6ms 红线,
        // 改为只滤波"标注区域外扩 3σ"的子块——卷积边界衰减带全部落在裁剪区之外,视觉无损。
        const float margin = BlurSigma * 3f;
        float sx = MathF.Max(r.X - margin - origin.X, 0);
        float sy = MathF.Max(r.Y - margin - origin.Y, 0);
        float ex = MathF.Min(r.X + r.Width + margin - origin.X, frozen.Width);
        float ey = MathF.Min(r.Y + r.Height + margin - origin.Y, frozen.Height);
        if (ex <= sx || ey <= sy) return;
        var src = new SKRect(sx, sy, ex, ey);                                    // 源(图像本地系)
        var dst = new SKRect(sx + origin.X, sy + origin.Y, ex + origin.X, ey + origin.Y); // 桌面系(canvas 已平移)
        canvas.Save();
        canvas.ClipRect(ToRect(r));
        canvas.DrawImage(frozen, src, dst, new SKSamplingOptions(SKFilterMode.Linear), paint);
        canvas.Restore();
    }

    /// <summary>选中态:青色虚线高亮框(外扩 3px)+ 8 锚点白方块。</summary>
    private static void DrawSelectionChrome(SKCanvas canvas, AnnotShape s, float dpi)
    {
        int pad = 3;
        var b = s.Bounds;
        var frame = new SKRect(b.X - pad, b.Y - pad, b.X + b.Width + pad, b.Y + b.Height + pad);
        using var box = new SKPaint
        {
            Color = new SKColor(0x32, 0xAD, 0xE6),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = false,
            PathEffect = SKPathEffect.CreateDash(new float[] { 5f, 4f }, 0f),
        };
        canvas.DrawRect(frame, box);

        float a = MathF.Max(6f, MathF.Round(8f * MathF.Max(1f, dpi)));
        using var anchorFill = new SKPaint { Color = SKColors.White, IsAntialias = false };
        using var anchorLine = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 160),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = false,
        };
        foreach (var c in OverlayGeometry.AnchorCenters(b))
        {
            var rect = new SKRect(c.X - a / 2f, c.Y - a / 2f, c.X + a / 2f, c.Y + a / 2f);
            canvas.DrawRect(rect, anchorFill);
            canvas.DrawRect(rect, anchorLine);
        }
    }

    // —————————————————— 工具条 ——————————————————

    /// <summary>绘制工具条。items/FrameRect 为桌面坐标(与 Layout 同系)。</summary>
    public static void DrawToolbar(SKCanvas canvas, AnnotToolbar tb, PxPoint windowOrigin,
        PxPoint? cursorDesktop, float dpi)
    {
        canvas.Save();
        canvas.Translate(-windowOrigin.X, -windowOrigin.Y);

        // 外框
        var frame = ToRect(tb.FrameRect);
        using var bg = new SKPaint { Color = new SKColor(28, 28, 30, 235), IsAntialias = true };
        using var frameLine = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 60),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
        };
        canvas.DrawRoundRect(frame, 6f, 6f, bg);
        canvas.DrawRoundRect(frame, 6f, 6f, frameLine);

        foreach (var it in tb.Items)
        {
            bool hover = cursorDesktop is { } cp &&
                cp.X >= it.Rect.X && cp.X < it.Rect.X + it.Rect.Width &&
                cp.Y >= it.Rect.Y && cp.Y < it.Rect.Y + it.Rect.Height;
            bool active = IsActive(tb, it);
            if (hover || active)
            {
                using var slot = new SKPaint { Color = active ? new SKColor(0x0A, 0x84, 0xFF, 230) : new SKColor(255, 255, 255, 40) };
                canvas.DrawRoundRect(ToRect(it.Rect), 4f, 4f, slot);
            }
            DrawItem(canvas, tb, it, dpi);
        }
        canvas.Restore();
    }

    private static bool IsActive(AnnotToolbar tb, AnToolbarItem it) => it.Action switch
    {
        AnToolbarAction.Tool => it.Index == -1 ? tb.ActiveTool is null : (int?)tb.ActiveTool == it.Index,
        AnToolbarAction.Color => tb.ColorIndex == it.Index,
        AnToolbarAction.Stroke => tb.Stroke == it.Index,
        _ => false,
    };

    private static void DrawItem(SKCanvas canvas, AnnotToolbar tb, AnToolbarItem it, float dpi)
    {
        var r = ToRect(it.Rect);
        var c = new SKPoint(r.MidX, r.MidY);
        bool activeSlot = IsActive(tb, it);
        using var icon = new SKPaint
        {
            Color = activeSlot ? SKColors.White : new SKColor(235, 235, 245),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1.5f, 1.8f * dpi),
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true,
        };
        float u = it.Rect.Width; // 槽位边长
        float q = u * 0.28f;     // 图标半尺寸

        switch (it.Action)
        {
            case AnToolbarAction.Tool:
                DrawToolIcon(canvas, it.Index == -1 ? null : (AnnotToolType)it.Index, c, q, icon, dpi);
                break;
            case AnToolbarAction.Color:
            {
                int argb = it.Index < 8 ? AnnotToolbar.Palette[it.Index]
                                        : AnnotToolbar.CustomCycle[tb.CustomCycleIndex % AnnotToolbar.CustomCycle.Length];
                using var fill = new SKPaint { Color = ToColor(argb), IsAntialias = true };
                var block = new SKRect(c.X - q, c.Y - q, c.X + q, c.Y + q);
                canvas.DrawRoundRect(block, 3f, 3f, fill);
                using var line = new SKPaint
                {
                    Color = new SKColor(255, 255, 255, 120),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1f,
                    IsAntialias = true,
                };
                canvas.DrawRoundRect(block, 3f, 3f, line);
                break;
            }
            case AnToolbarAction.Stroke:
            {
                float d = (it.Index + 1) * 1.4f * dpi;
                using var dot = new SKPaint { Color = icon.Color, IsAntialias = true };
                canvas.DrawCircle(c, d / 2f, dot);
                break;
            }
            case AnToolbarAction.Undo: // ↶ 左上折线箭头
            {
                var p1 = new SKPoint(c.X + q * 0.7f, c.Y + q * 0.6f);
                var p2 = new SKPoint(c.X - q * 0.7f, c.Y + q * 0.6f);
                canvas.DrawLine(p1, p2, icon);
                canvas.DrawLine(p2, new SKPoint(p2.X + q * 0.5f, c.Y + q * 0.1f), icon);
                canvas.DrawLine(p2, new SKPoint(p2.X + q * 0.5f, c.Y + q * 1.1f), icon);
                break;
            }
            case AnToolbarAction.Redo: // ↷ 镜像
            {
                var p1 = new SKPoint(c.X - q * 0.7f, c.Y + q * 0.6f);
                var p2 = new SKPoint(c.X + q * 0.7f, c.Y + q * 0.6f);
                canvas.DrawLine(p1, p2, icon);
                canvas.DrawLine(p2, new SKPoint(p2.X - q * 0.5f, c.Y + q * 0.1f), icon);
                canvas.DrawLine(p2, new SKPoint(p2.X - q * 0.5f, c.Y + q * 1.1f), icon);
                break;
            }
            case AnToolbarAction.Clear: // 空心方 + 斜杠
            {
                var box = new SKRect(c.X - q * 0.7f, c.Y - q * 0.7f, c.X + q * 0.7f, c.Y + q * 0.7f);
                canvas.DrawRect(box, icon);
                canvas.DrawLine(new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Top), icon);
                break;
            }
            case AnToolbarAction.Confirm: // ✓
            {
                canvas.DrawLine(new SKPoint(c.X - q * 0.8f, c.Y), new SKPoint(c.X - q * 0.1f, c.Y + q * 0.65f), icon);
                canvas.DrawLine(new SKPoint(c.X - q * 0.1f, c.Y + q * 0.65f), new SKPoint(c.X + q * 0.85f, c.Y - q * 0.6f), icon);
                break;
            }
            case AnToolbarAction.Cancel: // ✗
            {
                canvas.DrawLine(new SKPoint(c.X - q * 0.7f, c.Y - q * 0.7f), new SKPoint(c.X + q * 0.7f, c.Y + q * 0.7f), icon);
                canvas.DrawLine(new SKPoint(c.X + q * 0.7f, c.Y - q * 0.7f), new SKPoint(c.X - q * 0.7f, c.Y + q * 0.7f), icon);
                break;
            }
        }
    }

    private static void DrawToolIcon(SKCanvas canvas, AnnotToolType? tool, SKPoint c, float q, SKPaint icon, float dpi)
    {
        switch (tool)
        {
            case null: // 选择:箭头
                canvas.DrawLine(new SKPoint(c.X - q * 0.4f, c.Y - q * 0.8f), new SKPoint(c.X + q * 0.3f, c.Y + q * 0.7f), icon);
                canvas.DrawLine(new SKPoint(c.X + q * 0.3f, c.Y + q * 0.7f), new SKPoint(c.X + q * 0.05f, c.Y + q * 0.25f), icon);
                canvas.DrawLine(new SKPoint(c.X + q * 0.3f, c.Y + q * 0.7f), new SKPoint(c.X - q * 0.15f, c.Y + q * 0.55f), icon);
                break;
            case AnnotToolType.Rectangle:
                canvas.DrawRect(new SKRect(c.X - q, c.Y - q * 0.7f, c.X + q, c.Y + q * 0.7f), icon);
                break;
            case AnnotToolType.Ellipse:
                canvas.DrawOval(new SKRect(c.X - q, c.Y - q * 0.7f, c.X + q, c.Y + q * 0.7f), icon);
                break;
            case AnnotToolType.LineArrow:
            {
                var a = new SKPoint(c.X - q, c.Y + q * 0.7f);
                var b2 = new SKPoint(c.X + q, c.Y - q * 0.7f);
                canvas.DrawLine(a, b2, icon);
                DrawArrowHead(canvas, b2, a, q * 0.9f, icon);
                break;
            }
            case AnnotToolType.Polyline:
                canvas.DrawLine(new SKPoint(c.X - q, c.Y + q * 0.5f), new SKPoint(c.X - q * 0.2f, c.Y - q * 0.4f), icon);
                canvas.DrawLine(new SKPoint(c.X - q * 0.2f, c.Y - q * 0.4f), new SKPoint(c.X + q * 0.3f, c.Y + q * 0.2f), icon);
                canvas.DrawLine(new SKPoint(c.X + q * 0.3f, c.Y + q * 0.2f), new SKPoint(c.X + q, c.Y - q * 0.6f), icon);
                break;
            case AnnotToolType.Pen:
            {
                var pb = new SKPathBuilder();
                pb.MoveTo(new SKPoint(c.X - q, c.Y + q * 0.6f));
                pb.CubicTo(
                    new SKPoint(c.X - q * 0.3f, c.Y - q),
                    new SKPoint(c.X + q * 0.2f, c.Y + q),
                    new SKPoint(c.X + q, c.Y - q * 0.5f));
                using var path = pb.Snapshot();
                canvas.DrawPath(path, icon);
                break;
            }
            case AnnotToolType.Highlighter:
            {
                using var hl = new SKPaint
                {
                    Color = icon.Color,
                    StrokeWidth = MathF.Max(3f, icon.StrokeWidth * 2.6f),
                    StrokeCap = SKStrokeCap.Butt,
                    IsAntialias = false,
                };
                canvas.DrawLine(new SKPoint(c.X - q, c.Y + q * 0.3f), new SKPoint(c.X + q, c.Y - q * 0.3f), hl);
                break;
            }
            case AnnotToolType.Mosaic:
            {
                using var dot = new SKPaint { Color = icon.Color, IsAntialias = false };
                float s = q * 0.75f;
                canvas.DrawRect(c.X - s, c.Y - s, s, s, dot);
                canvas.DrawRect(c.X, c.Y, s, s, dot);
                canvas.DrawRect(c.X - s, c.Y + s * 0.15f, s, s, dot);
                canvas.DrawRect(c.X, c.Y + s * 1.15f, s, s, dot);
                break;
            }
            case AnnotToolType.Blur:
            {
                for (int i = 0; i < 3; i++)
                {
                    float yy = c.Y - q * 0.5f + i * q * 0.55f;
                    using var blurLine = new SKPaint
                    {
                        Color = icon.Color,
                        StrokeWidth = icon.StrokeWidth * (1f + i * 0.5f),
                        StrokeCap = SKStrokeCap.Round,
                        IsAntialias = true,
                    };
                    canvas.DrawLine(new SKPoint(c.X - q * 0.8f, yy), new SKPoint(c.X + q * 0.8f, yy), blurLine);
                }
                break;
            }
            case AnnotToolType.Eraser:
            {
                canvas.Save();
                canvas.Translate(c.X, c.Y);
                canvas.RotateDegrees(-30);
                canvas.DrawRect(new SKRect(-q * 0.9f, -q * 0.45f, q * 0.9f, q * 0.45f), icon);
                canvas.Restore();
                break;
            }
            case AnnotToolType.Number:
            {
                using var ring = new SKPaint { Color = icon.Color, Style = SKPaintStyle.Stroke, StrokeWidth = icon.StrokeWidth, IsAntialias = true };
                canvas.DrawCircle(c, q * 0.8f, ring);
                using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, q * 1.2f);
                canvas.DrawText("1", c.X, c.Y + q * 0.45f, SKTextAlign.Center, font,
                    new SKPaint { Color = icon.Color, IsAntialias = true });
                break;
            }
            case AnnotToolType.Text:
            {
                using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, q * 1.5f);
                canvas.DrawText("T", c.X, c.Y + q * 0.55f, SKTextAlign.Center, font,
                    new SKPaint { Color = icon.Color, IsAntialias = true });
                break;
            }
        }
    }

    /// <summary>元素悬停青框(§13:2px #00E5FF)。</summary>
    public static void DrawElementHighlight(SKCanvas canvas, PxRect bounds, PxPoint windowOrigin)
    {
        canvas.Save();
        canvas.Translate(-windowOrigin.X, -windowOrigin.Y);
        using var paint = new SKPaint
        {
            Color = new SKColor(0x00, 0xE5, 0xFF),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = false,
        };
        canvas.DrawRect(new SKRect(bounds.X, bounds.Y, bounds.X + bounds.Width, bounds.Y + bounds.Height), paint);
        canvas.Restore();
    }

    /// <summary>元素检测"检测中"提示:屏幕底部中央小字(§13 空响应软处理)。</summary>
    public static void DrawDetectingHint(SKCanvas canvas, int windowWidth, int windowHeight, float dpi)
    {
        float fontSize = MathF.Max(11f, 12f * dpi);
        using var font = new SKFont(UiTypeface, fontSize);
        using var paint = new SKPaint { Color = new SKColor(0x00, 0xE5, 0xFF, 210), IsAntialias = true };
        canvas.DrawText("元素检测中…", windowWidth / 2f, windowHeight - 12f * dpi, SKTextAlign.Center, font, paint);
    }
}
