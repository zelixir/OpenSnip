# OpenSnip M1 契约文档(SPEC)

> 本文档是 **唯一真源**。所有开发者(含子 agent)以本文件的接口签名为契约编程。
> M1 范围:截屏(F1)→遮罩选区(窗口高亮)→复制/保存/贴图(F3)→托盘+热键+命令行+单实例。标注工具是 M2,**不做**。
> 参考品:Snipaste 2.8.5(技术路线见 D:\Code\snipaste-eval\评估报告-CSharp版Snipaste技术路线.md)

## 0. 全局红线(违反=返工)

1. 所有项目 `TargetFramework=net10.0-windows`,`<Nullable>enable</Nullable>`,`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`(类库同理)。
2. **禁止 git 操作**(init/add/commit 一律由 Lead 收口时做)。
3. **禁止网络功能**(无下载/更新/遥测);NuGet 还原除外。
4. 大位图**只准驻非托管内存**(NativeMemory/SKBitmap/纹理),禁止 byte[]/Image 持有全屏数据。
5. 截屏主路径禁用 System.Drawing.Common(GDI 回退用原生 P/Invoke BitBlt)。
6. 各 agent **只写自己的文件域**(见 §6),不得触碰他人目录;发现契约问题,按契约继续实现并在完成消息中说明,由 Lead 裁决。
7. 类库项目不引用 WPF(Overlay/Pin/Core 都是纯 Win32+Skia);只有 App 引 WPF。
8. 中文注释、文件作用域命名空间(命名空间根 `OpenSnip.*`)。
9. csproj 只建自己的;解决方案 sln 由 Lead 收口时生成。App 引用类库的 ProjectReference 路径:`..\OpenSnip.Core\OpenSnip.Core.csproj` 等。

## 1. 项目结构(目录即文件域)

```
D:\Code\OpenSnip\
├── global.json                  # 已建(10.0.101)
├── src\OpenSnip.Core\           # [Agent A] P/Invoke + 截图引擎 + 窗口检测 + 位图 + 热键 + 剪贴板
├── src\OpenSnip.Overlay\        # [Agent B] 遮罩/选区窗口(ULW 自管 HWND + SkiaSharp)
├── src\OpenSnip.Pin\            # [Agent C] 贴图窗口(ULW + 缩放/透明/穿透/置顶)
├── src\OpenSnip.App\            # [Agent D] WPF 壳(托盘/热键绑定/命令行/单实例 IPC/保存)
├── tests\OpenSnip.Core.Tests\   # [Agent A] xUnit 冒烟
├── docs\                        # [Lead]
└── logs\                        # 运行日志输出目录(perf.log)
```

## 2. 接口签名冻结区(实现者照此写,调用者照此用)

### 2.1 Core 基础类型

```csharp
namespace OpenSnip.Core;

// 物理像素矩形(全项目坐标一律物理 px,Per-Monitor DPI V2 清单由 App 声明)
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public static PxRect Empty { get; }
    public bool Contains(int x, int y);
    public bool IsEmpty { get; }
}

public readonly record struct PxPoint(int X, int Y);

public sealed record DisplayInfo(
    int Index,            // 0-based,EnumDisplayMonitors 顺序
    string DeviceName,    // \\.\DISPLAY1
    PxRect Bounds,        // 物理像素虚拟桌面坐标
    PxRect WorkArea,
    bool IsPrimary,
    float DpiScale,       // 1.0/1.25/1.5...
    bool IsHdr);          // DXGI_COLOR_SPACE 检测,不可得时 false

public enum CaptureMode { DxgiDuplication, GdiBitBlt }
```

### 2.2 Imaging(非托管位图,格式固定 BGRA32 非预乘)

```csharp
public sealed class NativeBitmap : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }          // 4*width 向上对齐到 32 字节
    public IntPtr Scan0 { get; }        // NativeMemory.AlignedAlloc(64)
    public static NativeBitmap Allocate(int width, int height);
    public void CopyRegionFrom(NativeBitmap src, int srcX, int srcY); // 自左上角 (srcX,srcY) 起拷贝本尺寸区域
    public void Dispose();              // AlignedFree,防双释放
}

public static class BitmapConversion
{
    // 零拷贝包装:SkiaSharp 侧用 SKPixmap/SKBitmap wrap Scan0(注意 BGRA<->RGBA 转换在 Skia 色彩类型层面处理)
    public static SKBitmap WrapAsSkia(NativeBitmap bmp); // wrap 不拷贝,销毁顺序约定:先弃 SKBitmap 再 Dispose NativeBitmap
    public static byte[] EncodePng(NativeBitmap bmp);    // Skia 编码
    public static byte[] GetDibBytes(NativeBitmap bmp);  // 手工组 BITMAPINFOHEADER+像素(底朝上 BGRA)供 CF_DIB
}
```

### 2.3 Capture(DXGI DD 主路径 + BitBlt 回退)

```csharp
public sealed class DisplayManager
{
    public IReadOnlyList<DisplayInfo> Displays { get; }  // 构造时枚举
    public static DisplayManager Instance { get; }       // 懒加载单例
    public DisplayInfo? GetDisplayAt(PxPoint screenPt);
    public DisplayInfo? GetDisplayByIndex(int index);
}

public sealed class ScreenCaptureService : IDisposable
{
    public ScreenCaptureService();
    public CaptureMode LastUsedMode { get; }
    public bool DxgiAvailable { get; }
    // 单帧全屏捕获(冻结画面);显示索引失败时自动整屏 BitBlt 回退
    public NativeBitmap Capture(DisplayInfo display);
    // 预热:D3D 设备 + 每屏 DD 会话;启动时调用,消除首截抖动
    public void Warmup();
    public void Dispose();
}
```

### 2.4 窗口检测

```csharp
public sealed record WindowInfo(
    IntPtr Handle, string Title, string ClassName, string ExeName,
    PxRect Bounds, bool IsTopmost, bool IsToolWindow);

public static class WindowEnumerator
{
    // 桌面上可见可命中的顶层窗口,Z 序(顶在前);过滤:不可见/cloaked/WS_EX_TRANSPARENT/零尺寸/OwnsComposition
    public static IReadOnlyList<WindowInfo> GetHitTestableTopLevel();
}

public sealed class WindowHitTester
{
    // 返回覆盖 screenPt 的顶层窗口 Z 序列表(第一个=最顶);Overlay 悬停高亮用第 0 个,滚轮切层级用整个列表
    public IReadOnlyList<WindowInfo> HitTopToBottom(PxPoint screenPt);
}
```

### 2.5 全局热键

```csharp
public sealed class GlobalHotkeyManager : IDisposable
{
    public bool Register(int id, uint modifiers, uint virtualKey); // MOD_*|VK_*
    public void Unregister(int id);
    public event Action<int>? HotkeyPressed;  // 内部消息窗口线程触发
}
```

### 2.6 剪贴板

```csharp
public static class ClipboardWriter
{
    // 一次 Open 写双格式:CF_DIB + "PNG" 注册格式;调用线程需已 OLE 初始化(App 侧保证 STA)
    public static bool PutImage(NativeBitmap bmp);
}
```

### 2.7 性能日志

```csharp
public static class PerfLog
{
    public static void Log(string category, string message);          // 异步追加 logs\perf.log
    public static TimeSpan Measure(string category, string name, Action action);
    public static TimeSpan Measure<T>(string category, string name, Func<T> action);
}
```

### 2.8 Overlay(遮罩选区)

```csharp
namespace OpenSnip.Overlay;

public sealed class SnipResult
{
    public NativeBitmap Image { get; }        // 所选区域的裁剪(物理 px)
    public PxRect PhysicalRect { get; }       // 虚拟桌面物理坐标
    public DisplayInfo Display { get; }       // 选区主判定所在屏
}

public sealed class SnipOverlay : IDisposable
{
    // capture: 进入时冻结画面回调(内部会立即逐屏调用)
    public SnipOverlay(IReadOnlyList<DisplayInfo> displays, Func<DisplayInfo, NativeBitmap> capture);
    public void Start();                      // 创建内部 STA 线程,每屏建遮罩窗口并显示
    public event Action<SnipResult>? Confirmed;   // Enter/双击;在内部线程触发;触发后自动退出
    public event Action? Cancelled;               // Esc
    public void Close();                      // 线程安全,外部可强制关闭(视同 Cancelled)
}
```

### 2.9 Pin(贴图)

```csharp
namespace OpenSnip.Pin;

public sealed class PinWindow : IDisposable
{
    public PxRect CurrentRect { get; }        // 当前物理位置尺寸
    public bool Topmost { set; }
    public bool ClickThrough { set; }
    public byte OpacityPercent { set; }       // 10~100,LWA_ALPHA
    public void Close();
    public event Action<PinWindow>? Closed;
}

public static class PinManager
{
    // 单消息循环线程管理全部贴图窗口;initialRect 为显示位置(物理 px)
    public static PinWindow Pin(NativeBitmap image, PxRect initialRect);
    public static IReadOnlyList<PinWindow> ActivePins { get; }
    public static void Shutdown();            // 关全部并停线程
}
```

## 3. 各自实现要点(非冻结区,实现细节自定)

### 3.1 [Agent A] Core
- DXGI DD:Vortice.Direct3D11 + Vortice.DXGI(`dotnet add package Vortice.Direct3D11` 会连带 DXGI);D3D11CreateDevice(BGRA 支持)+ 枚举 IDXGIAdapter1/Output → IDXGIOutput1.DuplicateOutput;AcquireNextFrame 超时 100ms,拿不到即抛→上层回退。**每 DisplayInfo 缓存一个 duplication 会话**(重建逻辑:失败 3 次标记该屏回退 BitBlt)。
- BitBlt 回退:GetDC(null/monitor)→CreateCompatibleDC→CreateDIBSection(BGRA)→BitBlt(CAPTUREBLT 可选)→拷贝进 NativeBitmap。
- 窗口枚举:EnumWindows 回调委托存静态字段防 GC;DwmGetWindowAttribute(DWMWA_CLOAKED) 过滤;GetWindowLongW 检查 WS_EX_TRANSPARENT/WS_EX_TOOLWINDOW;进程名用 QueryFullProcessImageNameW。
- HitTester:GetHitTestableTopLevel() 按 WindowFromPoint/自身 Z 序排序 + PxRect.Contains 判定(允许负坐标虚拟桌面)。
- 剪贴板:OpenClipboard(重试 5 次×10ms)/EmptyClipboard/SetClipboardData(CF_DIB + RegisterClipboardFormat("PNG"))/CloseClipboard。
- 单测(冒烟级):NativeBitmap 分配/释放、GetHitTestableTopLevel 返回非空且含自身进程外窗口、DisplayManager.Displays 非空、GetDibBytes 头部字段正确。**单测不准弹 UI、不写剪贴板**。

### 3.2 [Agent B] Overlay
- 每显示器一个 ULW 窗口:WS_POPUP|WS_EX_LAYERED|WS_EX_TOPMOST|WS_EX_TOOLWINDOW,尺寸=该屏物理 Bounds;注册类名 "OpenSnip.Overlay"。
- 消息循环:所有遮罩窗口跑在 SnipOverlay 的单个 STA 线程(GetMessage 循环,WM_CLOSE→退出码)。
- 冻结画面:Start() 后先回调 capture 逐屏取 NativeBitmap,Skia 画背景+50% 黑遮罩+选区外加深;选区绘制:边框 1px 红(#FF0000)+ 8 锚点白方块+右上尺寸标签 "W×H (px)";十字准线全屏(1px,半透明白)。
- 窗口高亮:鼠标空闲(无拖拽)时用 WindowHitTester.HitTopToBottom(pt)[0] 画 2px 蓝框(#00A8FF)+ 半透明填充;按下拖拽后切换手选模式,高亮消失。
- 放大镜:鼠标附近左下角 160px 圆形/方形放大 8 倍(取冻结位图像素最近邻),下方显示 HEX 色值。
- 交互:左键按下→拖拽→松开=完成初选;松开后可拖 8 锚点调整、方向键微调 1px;Enter/双击=Confirmed;Esc=Cancelled;确认时从冻结位图 CopyRegionFrom 裁剪。
- 性能红线:拖拽时重绘(位图合成+ULW 提交)全路径 <6ms @2K 屏;实现=SKCanvas 直接画进 wrap 的 SKBitmap,仅失效矩形全量提交(简化:全窗口 ULW,单帧即走)。
- PerfLog 打点:capture 耗时、首帧 ULW 完成时刻(相对 Start())、确认→裁剪完成。

### 3.3 [Agent C] Pin
- PinManager:单一 STA 线程 + GetMessage 循环,所有 PinWindow HWND 挂其上;Pin() 通过线程队列投递创建。
- 窗口:WS_POPUP|WS_EX_LAYERED|WS_EX_TOPMOST|WS_EX_TOOLWINDOW;内容用 Skia 画进 ULW DIB(初始=整图 100%);缩放走位图重采样(SKBitmap.Resize, quality mipmap)。
- 交互:拖动移动;滚轮=缩放 1.1 步进(指针为锚点);Ctrl+滚轮=透明度;双击=Close;右键菜单(自绘 Win32 弹出菜单或 TrackPopupMenu):置顶开关/鼠标穿透/保存为 PNG(M2 接文件对话框,v1 仅置顶+穿透+关闭)/关闭;窗口边缘 8px 拖拽缩放 v1 可省;贴边吸附:距离屏幕边 <20×DPI 时吸附。
- 穿透:SetWindowLongW(WS_EX_TRANSPARENT) 切换;Opacity:SetLayeredWindowAttributes(LWA_ALPHA)。
- 关闭时 NativeBitmap 所有权约定:PinWindow 持有传入位图,Close 后 Dispose。Closed 事件在 pin 线程触发。

### 3.4 [Agent D] App(WPF 壳)
- app.manifest:PerMonitorV2 dpiAwareness + requestedExecutionLevel asInvoker。
- 托盘:H.NotifyIcon 包;菜单:截屏(F1)/贴图(F3)/退出;双击图标=截屏。
- 热键:GlobalHotkeyManager 注册 F1(截屏)/F3(贴图);贴图=读剪贴板图像(读 CF_DIB/PNG/文件列表,v1 至少支持图像与文件路径),PinManager.Pin 到主屏中心。
- 单实例:命名管道 `OpenSnip.Ipc`;第二实例把完整命令行发给首实例后退出;`--block`:等首实例回写完成事件(超时 60s)。
- 命令行 v1(接受到首实例后解析执行):
  - `snip` / `snip --full` / `snip --area X Y W H` / `snip --delay N`
  - `-o clipboard|pin|<FILE路径>`(可组合,分号分隔,顺序:FILE→clipboard→pin)
  - `paste` / `exit`
- 截屏流程:构造 SnipOverlay(传 ScreenCaptureService.Capture)→Start()→Confirmed:PutImage/存文件/PinManager.Pin→PerfLog;Cancelled:释放位图。
- 预热:App 启动 3s 后后台线程 ScreenCaptureService.Warmup() + 预加载 DisplayManager。
- 保存:SaveFileDialog(WPF 内置),默认文件名 `Snip_yyyy-MM-dd_HH-mm-ss.png`。
- PerfLog 打点:进程启动→托盘就绪、热键→Confirmed 全链路。

## 4. NuGet 依赖

- Core:Vortice.Direct3D11(最新稳定)、无其他
- Overlay:SkiaSharp(3.x 最新稳定)+ 依赖 Core
- Pin:SkiaSharp + 依赖 Core
- App:H.NotifyIcon + 依赖 Core/Overlay/Pin
- Tests:xunit + xunit.runner.visualstudio + 依赖 Core
- 版本冲突由 Lead 收口统一仲裁,先各自 `dotnet add package <名>` 默认最新即可。

## 5. 验收(M1 Definition of Done)

1. `dotnet build` 全绿(含 tests);`dotnet test` 过。
2. 启动 App:托盘出现,无崩溃;F1→遮罩出现(冻结画面);窗口悬停高亮;拖拽选区+锚点调整;Enter→画图 Ctrl+V 可粘贴;Esc→取消。
3. `OpenSnip.App snip --full -o clipboard` 命令行链路通。
4. F3 贴图:置顶/滚轮缩放/Ctrl 滚轮透明/穿透/双击关。
5. logs\perf.log 有三类打点(Overlay 捕获/确认、App 全链路、Core 回退标记)。
6. 无 `.csproj` 引用 System.Drawing.Common;无网络代码。

## 6. 交付与沟通

- 完成时:在最终消息中列出:创建的文件清单、build 结果(命令与输出摘要)、偏离契约之处(如有)、待联调风险点。
- 不要等待其他 agent:严格按本契约并行实现;编译期缺依赖属于预期,收口由 Lead 统一修复。
- 有阻塞超过 30 分钟仍未解决,发消息给 lead 并附上具体错误。