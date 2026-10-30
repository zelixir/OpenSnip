// 渲染性能探针(临时,不进仓库):
// 模拟 2K(2560x1440)冻结画面,走 Overlay 真实代码路径
// (OverlayRenderer.DrawFrame + OverlayWindow.RenderFrame/Commit,经反射调 internal),
// 测:纯 Skia 绘制 / ULW 提交 / 全路径,验证 <6ms @2K 红线。
// ULW 窗口置于屏外 (-2000,-2000) 且从不 ShowWindow,用户无感知。
using System.Diagnostics;
using System.Reflection;
using OpenSnip.Core;
using OpenSnip.Overlay;
using SkiaSharp;

const int W = 2560, H = 1440;
var sw = new Stopwatch();

// 1) 伪造 2K 冻结画面:渐变 + 200 个随机"窗口"块(给 Skia 足够的绘制压力)
using var frozen = NativeBitmap.Allocate(W, H);
unsafe
{
    byte* p = (byte*)frozen.Scan0;
    for (int y = 0; y < H; y++)
    {
        byte* row = p + (long)y * frozen.Stride;
        for (int x = 0; x < W; x++)
        {
            row[x * 4 + 0] = (byte)(x & 0xFF);
            row[x * 4 + 1] = (byte)(y & 0xFF);
            row[x * 4 + 2] = (byte)((x ^ y) & 0xFF);
            row[x * 4 + 3] = 255;
        }
    }
    var rand = new Random(42);
    for (int i = 0; i < 200; i++)
    {
        int rx = rand.Next(W - 400), ry = rand.Next(H - 300), rw = rand.Next(100, 400), rh = rand.Next(80, 300);
        byte b = (byte)rand.Next(256), g = (byte)rand.Next(256), r = (byte)rand.Next(256);
        for (int y = ry; y < Math.Min(ry + rh, H); y++)
        {
            byte* row = p + (long)y * frozen.Stride;
            for (int x = rx; x < Math.Min(rx + rw, W); x++)
            { row[x * 4] = b; row[x * 4 + 1] = g; row[x * 4 + 2] = r; row[x * 4 + 3] = 255; }
        }
    }
}

// 2) 与 OverlayWindow.SetFrozen 相同的零拷贝链路
using var skFrozen = BitmapConversion.WrapAsSkia(frozen);
var pixmap = skFrozen.PeekPixels() ?? throw new InvalidOperationException("PeekPixels null");
using var frozenImage = SKImage.FromPixels(pixmap);

// 3) 背衬 SKBitmap(预乘 BGRA)+ SKCanvas,与 OverlayWindow 一致
using var back = new SKBitmap(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
using var canvas = new SKCanvas(back);

// —— 组件级基准:定位逐帧成本(blit/遮罩矩形/预乘转换收益)——
using var premulBmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
using (var pc = new SKCanvas(premulBmp))
{
    pc.DrawImage(frozenImage, new SKRect(0, 0, W, H), new SKRect(0, 0, W, H), SKSamplingOptions.Default, new SKPaint { IsAntialias = false });
    pc.Flush();
}
using var premulImg = SKImage.FromPixels(premulBmp.PeekPixels()!);

var fullRect = new SKRect(0, 0, W, H);
var plainPaint = new SKPaint { IsAntialias = false };
var srcPaint = new SKPaint { Color = new SKColor(0, 0, 0, 177), BlendMode = SKBlendMode.Src, IsAntialias = false };

void Bench(string name, Action act, int n = 200)
{
    var b = new double[n];
    for (int i = 0; i < n; i++)
    {
        sw.Restart(); act(); sw.Stop();
        b[i] = sw.Elapsed.TotalMilliseconds;
    }
    Array.Sort(b);
    Console.WriteLine($"{name,-26} avg={b.Average(),7:F3}ms  p95={b[(int)(n * 0.95)],7:F3}ms  max={b[^1],7:F3}ms");
}

Console.WriteLine("--- 组件基准 ---");
Bench("blit unpremul(SrcOver)", () => { canvas.DrawImage(frozenImage, fullRect, fullRect, SKSamplingOptions.Default, plainPaint); canvas.Flush(); });
Bench("blit premul(SrcOver)", () => { canvas.DrawImage(premulImg, fullRect, fullRect, SKSamplingOptions.Default, plainPaint); canvas.Flush(); });
Bench("4x darken rects(Src)", () =>
{
    canvas.DrawRect(new SKRect(0, 0, W, 300), srcPaint);
    canvas.DrawRect(new SKRect(0, 1000, W, H - 1000), srcPaint);
    canvas.DrawRect(new SKRect(0, 300, 600, 700), srcPaint);
    canvas.DrawRect(new SKRect(1700, 300, W - 1700, 700), srcPaint);
    canvas.Flush();
});
Bench("full mask rect(Src a=128)", () => { canvas.DrawRect(fullRect, new SKPaint { Color = new SKColor(0, 0, 0, 128), BlendMode = SKBlendMode.Src }); canvas.Flush(); });



// 4) 反射取 internal 渲染器 / 模型 / 窗口
var asm = typeof(SnipOverlay).Assembly;
var rendererType = asm.GetType("OpenSnip.Overlay.OverlayRenderer", throwOnError: true)!;
var modelType = asm.GetType("OpenSnip.Overlay.RenderModel", throwOnError: true)!;
var windowType = asm.GetType("OpenSnip.Overlay.OverlayWindow", throwOnError: true)!;
var renderer = Activator.CreateInstance(rendererType, nonPublic: true)!;
var drawMethod = rendererType.GetMethod("DrawFrame", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

object MakeModel(PxPoint? cursor) =>
    Activator.CreateInstance(modelType, BindingFlags.Public | BindingFlags.Instance, binder: null,
        new object?[]
        {
            new PxPoint(0, 0),          // WindowOrigin
            W, H,                       // 尺寸
            premulImg,                  // Frozen(与生产路径一致:经 SetFrozen 预乘转存后的图)
            new PxRect(600, 300, 1100, 700), // Selection
            null,                       // HighlightWindow(拖拽中)
            cursor,                     // CursorPt
            "#A1B2C3",                  // MagnifierHex
            cursor is not null,         // ShowMagnifier
            1.0f,                       // DpiScale
        }, culture: null)!;

var model = MakeModel(new PxPoint(1150, 650));

// 5) 纯 Skia 绘制路径(拖拽重绘的合成部分)
var drawTimes = new double[300];
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
for (int i = 0; i < drawTimes.Length; i++)
{
    sw.Restart();
    drawMethod.Invoke(renderer, new object[] { canvas, model });
    sw.Stop();
    drawTimes[i] = sw.Elapsed.TotalMilliseconds;
}

// 6) 真实 ULW 窗口(屏外隐藏)走 RenderFrame(=draw+Commit 全路径)
var createMethod = windowType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
var fakeDisplay = new DisplayInfo(0, "\\\\.\\PROBE",
    new PxRect(-2000, -2000, W, H), new PxRect(-2000, -2000, W, H), IsPrimary: false, DpiScale: 1f, IsHdr: false);
var win = createMethod.Invoke(null, new object[] { fakeDisplay })!;
windowType.GetMethod("SetFrozen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
    .Invoke(win, new object[] { frozen });
var renderFrame = windowType.GetMethod("RenderFrame", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
var commit = windowType.GetMethod("Commit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

// 首帧(排除初始化抖动后再单独测)
renderFrame.Invoke(win, new object[] { renderer, model });

var frameTimes = new double[150];
for (int i = 0; i < frameTimes.Length; i++)
{
    sw.Restart();
    renderFrame.Invoke(win, new object[] { renderer, model });
    sw.Stop();
    frameTimes[i] = sw.Elapsed.TotalMilliseconds;
}

// 7) 仅 ULW 提交(位图已渲染好)
var commitTimes = new double[150];
for (int i = 0; i < commitTimes.Length; i++)
{
    sw.Restart();
    commit.Invoke(win, null);
    sw.Stop();
    commitTimes[i] = sw.Elapsed.TotalMilliseconds;
}


windowType.GetMethod("Dispose", Type.EmptyTypes)!.Invoke(win, null);

// 8) 统计输出
static void Report(string name, double[] xs)
{
    var sorted = (double[])xs.Clone();
    Array.Sort(sorted);
    double avg = sorted.Average();
    double p50 = sorted[sorted.Length / 2];
    double p95 = sorted[(int)(sorted.Length * 0.95)];
    double max = sorted[^1];
    Console.WriteLine($"{name,-22} avg={avg,7:F3}ms  p50={p50,7:F3}ms  p95={p95,7:F3}ms  max={max,7:F3}ms  n={xs.Length}");
}

Console.WriteLine($"=== OpenSnip.Overlay 渲染性能探针 @ {W}x{H} ===");
Report("Skia draw(合成)", drawTimes);
Report("draw+ULW(全路径)", frameTimes);
Report("ULW commit only", commitTimes);
double worst = Math.Max(frameTimes.Average(), 0);
Console.WriteLine(worst < 6.0 ? $"VERDICT: 全路径均值 {frameTimes.Average():F3}ms < 6ms 红线 OK"
                              : $"VERDICT: 全路径均值 {frameTimes.Average():F3}ms 超出 6ms 红线!");
