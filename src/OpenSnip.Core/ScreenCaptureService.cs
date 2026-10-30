using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace OpenSnip.Core;

/// <summary>
/// 截屏服务:DXGI Desktop Duplication 主路径(每显示器缓存会话),失败自动 GDI BitBlt 回退。
/// 所有坐标为物理像素。线程模型:内部一把锁串行化(Warmup 与 Capture 可能来自不同线程)。
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<int, DuplicationSession> _sessions = new(); // key: DisplayInfo.Index
    private readonly Dictionary<int, int> _createFailures = new();          // 会话"创建"连续失败计数
    private CaptureMode _lastUsedMode = CaptureMode.GdiBitBlt;
    private double _lastContentRatio;
    private bool _disposed;

    /// <summary>最近一次 Capture 的非黑像素采样占比 [0,1](帧有效性自检指标,附加诊断 API)。</summary>
    public double LastContentRatio
    {
        get { lock (_gate) return _lastContentRatio; }
    }


    public ScreenCaptureService()
    {
    }

    /// <summary>上一次捕获实际使用的模式。</summary>
    public CaptureMode LastUsedMode
    {
        get { lock (_gate) return _lastUsedMode; }
    }

    /// <summary>DXGI Desktop Duplication 是否可用(存在未禁用的会话)。</summary>
    public bool DxgiAvailable
    {
        get { lock (_gate) return _sessions.Values.Any(s => !s.Disabled); }
    }

    /// <summary>预热:D3D 设备 + 每屏 DD 会话;启动时调用,消除首截抖动。</summary>
    public void Warmup()
    {
        lock (_gate)
        {
            foreach (var display in DisplayManager.Instance.Displays)
            {
                TryCreateSession(display);
            }
            PerfLog.Log("Core", $"Warmup 完成,DD 会话 {_sessions.Count} 个,可用={DxgiAvailable}");
        }
    }

    /// <summary>
    /// 单帧全屏捕获(冻结画面):优先走缓存的 duplication 会话;
    /// 该屏失败累计 3 次后永久标记回退 BitBlt(契约 §3.1)。
    /// </summary>
    public NativeBitmap Capture(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ScreenCaptureService));
            }

            if (!_sessions.TryGetValue(display.Index, out var session))
            {
                session = TryCreateSession(display);
            }

            if (session is not null && !session.Disabled)
            {
                var swDxgi = Stopwatch.StartNew();
                try
                {
                    var bmp = session.CaptureFrame();
                    swDxgi.Stop();
                    _lastUsedMode = CaptureMode.DxgiDuplication;
                    _lastContentRatio = ContentProbe.NonBlackRatio(bmp);
                    PerfLog.Log("Core",
                        $"Capture DXGI display={display.Index}({display.DeviceName}) " +
                        $"{display.Bounds.Width}x{display.Bounds.Height} {swDxgi.Elapsed.TotalMilliseconds:F2} ms " +
                        $"非黑占比={_lastContentRatio:P1}");
                    return bmp;
                }
                catch (Exception ex)
                {
                    if (!session.FallbackMarked && session.ConsecutiveFailures >= 3)
                    {
                        session.FallbackMarked = true;
                        // §5.5 要求的"Core 回退标记"打点
                        PerfLog.Log("Core",
                            $"回退标记 display={display.Index}({display.DeviceName}) DD 连续失败 " +
                            $"{session.ConsecutiveFailures} 次,后续全部走 BitBlt。最后错误:{ex.Message}");
                    }
                    else
                    {
                        PerfLog.Log("Core",
                            $"DXGI 失败 display={display.Index} 第 {session.ConsecutiveFailures}/3 次:{ex.Message}");
                    }
                }
            }

            // BitBlt 回退
            var sw = Stopwatch.StartNew();
            var gdi = GdiCapture.Capture(display);
            sw.Stop();
            _lastUsedMode = CaptureMode.GdiBitBlt;
            _lastContentRatio = ContentProbe.NonBlackRatio(gdi);
            PerfLog.Log("Core",
                $"Capture BitBlt display={display.Index}({display.DeviceName}) " +
                $"{display.Bounds.Width}x{display.Bounds.Height} {sw.Elapsed.TotalMilliseconds:F2} ms " +
                $"非黑占比={_lastContentRatio:P1}");
            return gdi;
        }
    }

    /// <summary>
    /// 尝试为指定屏建立 duplication 会话。
    /// 创建连续失败 3 次 → 放置 Disabled 占位会话(§3.1"失败 3 次标记该屏回退 BitBlt"),不再逐帧重试。
    /// </summary>
    private DuplicationSession? TryCreateSession(DisplayInfo display)
    {
        _createFailures.TryGetValue(display.Index, out int failures);
        if (failures >= 3)
        {
            var disabled = new DuplicationSession(display) { FallbackMarked = true };
            _sessions[display.Index] = disabled;
            _createFailures.Remove(display.Index);
            // §5.5 要求的"Core 回退标记"打点(创建路径)
            PerfLog.Log("Core",
                $"回退标记 display={display.Index}({display.DeviceName}) DD 会话创建连续失败 {failures} 次,后续全部走 BitBlt。");
            return disabled;
        }
        try
        {
            var session = new DuplicationSession(display);
            session.EnsureSession();
            _sessions[display.Index] = session;
            _createFailures.Remove(display.Index);
            return session;
        }
        catch (Exception ex)
        {
            _createFailures[display.Index] = failures + 1;
            PerfLog.Log("Core",
                $"创建 DD 会话失败 display={display.Index}({display.DeviceName}) 第 {failures + 1}/3 次:{ex.Message.Replace("\r", " ").Replace("\n", " ")}");
            return null;
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
            foreach (var s in _sessions.Values)
            {
                s.Dispose();
            }
            _sessions.Clear();
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 单显示器 duplication 会话缓存:D3D 设备 + 上下文 + staging 纹理 + IDXGIOutputDuplication。
/// 策略:温会话 10ms 短探测,超时重建 duplication(新会话首帧必返回当前桌面)再按契约 100ms 捕获;
/// 仍失败按"会话失败"上抛,由服务计数并回退。
/// </summary>
internal sealed class DuplicationSession : IDisposable
{
    /// <summary>标准特性级别表(高→低,原生 API 要求降级尝试顺序)。</summary>
    internal static readonly uint[] D3DFeatureLevels =
    {
        (uint)Vortice.Direct3D.FeatureLevel.Level_11_1, // 0xB100
        (uint)Vortice.Direct3D.FeatureLevel.Level_11_0, // 0xB000
        (uint)Vortice.Direct3D.FeatureLevel.Level_10_1, // 0xA100
        (uint)Vortice.Direct3D.FeatureLevel.Level_10_0, // 0xA000
    };

    private readonly DisplayInfo _display;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private bool _disposed;

    /// <summary>连续失败次数(成功后清零)。</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>已达到 3 次失败、永久回退 BitBlt 标记(由 ScreenCaptureService 置位)。</summary>
    public bool FallbackMarked { get; set; }

    /// <summary>禁用(= 已回退),不再尝试 DXGI。</summary>
    public bool Disabled => FallbackMarked;

    public DuplicationSession(DisplayInfo display)
    {
        _display = display;
    }

    /// <summary>当前 duplication 是否已成功取过帧(决定探测超时)。</summary>
    private bool _acquiredOnce;

    /// <summary>
    /// 最近有效帧缓存(每屏一份,非托管):静止桌面时 AcquireNextFrame 超时,
    /// 缓存 == 当前桌面,直接克隆返回(最快路径,不碰 GPU)。仅"实时取到的新帧"才更新。
    /// </summary>
    private NativeBitmap? _lastValidFrame;

    /// <summary>本帧是否来自实时 acquire(false=缓存克隆;只有实时帧才值得更新缓存)。</summary>
    private bool _frameIsLive;

    /// <summary>把验证过内容的帧写入缓存(分辨率变化时自动重建缓存)。</summary>
    private void StoreCache(NativeBitmap bmp)
    {
        if (_lastValidFrame is null || _lastValidFrame.Width != bmp.Width || _lastValidFrame.Height != bmp.Height)
        {
            _lastValidFrame?.Dispose();
            _lastValidFrame = NativeBitmap.Allocate(bmp.Width, bmp.Height);
        }
        _lastValidFrame.CopyRegionFrom(bmp, 0, 0);
    }

    /// <summary>捕获一帧到新的 NativeBitmap;带黑帧自检(非黑占比 <0.1% 视为黑帧,重建重试最多 3 次)。</summary>
    public NativeBitmap CaptureFrame()
    {
        if (Disabled)
        {
            throw new InvalidOperationException("该屏已标记回退 BitBlt。");
        }
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            NativeBitmap bmp;
            try
            {
                bmp = TryAcquireAndCopy();
            }
            catch
            {
                ConsecutiveFailures++;
                if (ConsecutiveFailures >= 3)
                {
                    FallbackMarked = true;
                }
                ReleaseDuplicationOnly(); // 丢 duplication,下次重建;设备/纹理视健康情况复用
                throw;
            }

            double ratio = ContentProbe.NonBlackRatio(bmp);
            if (ratio >= 0.001)
            {
                ConsecutiveFailures = 0;
                if (_frameIsLive)
                {
                    StoreCache(bmp); // 只有实时帧才更新缓存(缓存克隆无需回写)
                }
                if (attempt > 1)
                {
                    PerfLog.Log("Core", $"DD 黑帧重试成功 attempt={attempt} 非黑占比={ratio:P1}");
                }
                return bmp;
            }

            // 全黑:丢弃 → 重建 duplication → 重取(防驱动黑帧/空表面漏网)
            bmp.Dispose();
            ReleaseDuplicationOnly();
            PerfLog.Log("Core", $"DD 疑似黑帧 非黑占比={ratio:P2} attempt={attempt}/3,重建 duplication 重试");
        }
        ConsecutiveFailures++;
        if (ConsecutiveFailures >= 3)
        {
            FallbackMarked = true;
        }
        throw new InvalidOperationException("DXGI 连续 3 帧非黑占比 <0.1%(疑似空表面/驱动黑帧),转 BitBlt。");
    }

    /// <summary>确保设备/staging/duplication 就绪;健康的部分直接复用(设备创建 ~70ms,由 Warmup 吸收)。</summary>
    public void EnsureSession()
    {
        bool deviceHealthy = _device is not null && _context is not null && _staging is not null
                             && _device.DeviceRemovedReason.Success;
        if (!deviceHealthy)
        {
            // 设备被移除(驱动重置/HDR 切换等)或首次调用:全量重建
            Dispose();
            _disposed = false;
            CreateDeviceAndStaging();
        }
        if (_duplication is null)
        {
            EnsureDuplication();
        }
    }

    /// <summary>
    /// 仅重建 duplication(≈1ms,设备/staging 保持缓存):新 duplication 的首帧
    /// 必定返回当前完整桌面,是"冻结画面"语义下最确定、最快的取帧方式。
    /// </summary>
    private void EnsureDuplication()
    {
        if (_duplication is not null)
        {
            return;
        }
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    if (output is null)
                    {
                        continue;
                    }
                    using (output)
                    {
                        var outDesc = output.Description;
                        if (!string.Equals(outDesc.DeviceName, _display.DeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        _duplication = output1.DuplicateOutput(_device);
                        _acquiredOnce = false;
                        return;
                    }
                }
            }
        }
        throw new InvalidOperationException($"全部适配器上未找到显示器 {_display.DeviceName} 的 DXGI 输出。");
    }

    /// <summary>
    /// 建设备 + staging(一次性,昂贵 ~70ms):
    /// DuplicateOutput 严格校验设备必须建在"拥有该输出的适配器"上(虚拟屏/多显卡均如此),
    /// 所以枚举 factory 找到目标输出,用原生 P/Invoke 把设备建在该适配器上。
    /// </summary>
    private void CreateDeviceAndStaging()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var adapterDesc = adapter.Description.Description;
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    if (output is null)
                    {
                        continue;
                    }
                    using (output)
                    {
                        var outDesc = output.Description;
                        PerfLog.Log("Core",
                            $"DD 枚举 adapter={a}({adapterDesc}) output={o}({outDesc.DeviceName}) " +
                            $"attached={outDesc.AttachedToDesktop} 目标={_display.DeviceName}");
                        if (!string.Equals(outDesc.DeviceName, _display.DeviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // adapter 非空时 driverType 必须 D3D_DRIVER_TYPE_UNKNOWN(0),Win32 契约
                        // PreserveSig=false:HRESULT 失败直接抛 COMException
                        PerfLog.Log("Core", $"DD 建设备 adapter={a}({adapterDesc})...");
                        NativeMethods.D3D11CreateDeviceRaw(
                            adapter.NativePointer, 0 /* UNKNOWN */, IntPtr.Zero,
                            (uint)DeviceCreationFlags.BgraSupport,
                            D3DFeatureLevels, (uint)D3DFeatureLevels.Length, 7 /* D3D11_SDK_VERSION */,
                            out nint devicePtr, out _, out nint contextPtr);
                        _device = new ID3D11Device(devicePtr);        // 接管返回的引用
                        _context = new ID3D11DeviceContext(contextPtr);
                        PerfLog.Log("Core", $"DD 建设备 OK({_device.FeatureLevel})");

                        var desc = new Texture2DDescription
                        {
                            Width = (uint)_display.Bounds.Width,
                            Height = (uint)_display.Bounds.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm, // 桌面复制固定输出格式
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            MiscFlags = ResourceOptionFlags.None,
                        };
                        PerfLog.Log("Core",
                            $"DD staging desc w={desc.Width} h={desc.Height} mip={desc.MipLevels} arr={desc.ArraySize} " +
                            $"fmt={(int)desc.Format} usage={desc.Usage} bind={desc.BindFlags} cpu={desc.CPUAccessFlags} misc={desc.MiscFlags}");
                        try
                        {
                            _staging = _device.CreateTexture2D(in desc); // staging 无初始数据(pInitialData=NULL)
                        }
                        catch (Exception ex)
                        {
                            PerfLog.Log("Core", $"DD staging 异常:{ex.GetType().FullName} {ex.Message.Replace("\r", " ").Replace("\n", " ")}\n{ex.StackTrace}");
                            throw;
                        }
                        PerfLog.Log("Core", "DD staging OK");
                        GC.KeepAlive(adapter);
                        return; // duplication 由 EnsureDuplication 按需快速重建(≈1ms)
                    }
                }
            }
        }
        throw new InvalidOperationException($"全部适配器上未找到显示器 {_display.DeviceName} 的 DXGI 输出。");
    }

    private NativeBitmap TryAcquireAndCopy()
    {
        EnsureSession();

        // 路径 1:0ms 短探测——桌面有合成更新则立即拿到实时帧(整幅表面,非增量)
        if (TryAcquireFrame(0, out var res, out var info))
        {
            // 新建 duplication 的空表面帧竞态:首次合成完成前驱动可能交 S_OK 全零帧,
            // 用 frameInfo 甄别(仅对"尚未成功取过帧"的新会话生效)
            if (!_acquiredOnce && info.LastPresentTime == 0 && info.AccumulatedFrames == 0)
            {
                ReleaseFrame(res);
                res.Dispose();
                // 静止桌面 + 新会话:等首次合成(最多 100ms,拿不到即抛→上层回退)
                if (!TryAcquireFrame(100, out res, out info))
                {
                    throw new TimeoutException("AcquireNextFrame 等待首次合成超时(100ms)。");
                }
            }
            _acquiredOnce = true;
            _frameIsLive = true;
            var bmp = CopyFrameToBitmap(res);
            ReleaseFrame(res);
            res.Dispose();
            return bmp;
        }

        // 路径 2:超时 = 桌面自上次捕获以来静止 → 最近有效帧 == 当前桌面,直接命中缓存(最快,不碰 GPU)
        if (_lastValidFrame is not null)
        {
            _frameIsLive = false;
            var clone = NativeBitmap.Allocate(_lastValidFrame.Width, _lastValidFrame.Height);
            clone.CopyRegionFrom(_lastValidFrame, 0, 0);
            return clone;
        }

        // 路径 3:冷启动无缓存——按契约 100ms 等首帧(含空表面帧甄别)
        IDXGIResource? valid = null;
        for (int i = 0; i < 4; i++)
        {
            if (!TryAcquireFrame(100, out var coldRes, out var coldInfo))
            {
                throw new TimeoutException("AcquireNextFrame 超时(100ms)。");
            }
            if (coldInfo.LastPresentTime == 0 && coldInfo.AccumulatedFrames == 0)
            {
                ReleaseFrame(coldRes);
                coldRes.Dispose();
                continue;
            }
            valid = coldRes;
            break;
        }
        if (valid is null)
        {
            throw new InvalidOperationException("AcquireNextFrame 连续 4 次返回无呈现内容帧(空表面)。");
        }
        _acquiredOnce = true;
        _frameIsLive = true;
        var cold = CopyFrameToBitmap(valid);
        ReleaseFrame(valid);
        valid.Dispose();
        return cold;
    }

    private void ReleaseFrame(IDXGIResource resource)
    {
        try
        {
            _duplication!.ReleaseFrame();
        }
        catch
        {
            // 释放失败通常伴随 AccessLost,下轮重建即可
        }
    }

    /// <summary>帧纹理 → 新 NativeBitmap(CopyResource → Map → 行拷贝)。</summary>
    private NativeBitmap CopyFrameToBitmap(IDXGIResource resource)
    {
        using var frameTexture = resource.QueryInterface<ID3D11Texture2D>();
        var bmp = NativeBitmap.Allocate(_display.Bounds.Width, _display.Bounds.Height);
        try
        {
            _context!.CopyResource(_staging!, frameTexture);
            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    byte* dst = (byte*)bmp.Scan0;
                    int rowBytes = (int)Math.Min(mapped.RowPitch, (long)bmp.Width * 4);
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        Buffer.MemoryCopy(
                            src + (long)y * mapped.RowPitch,
                            dst + (long)y * bmp.Stride,
                            (ulong)bmp.Stride,
                            (ulong)rowBytes);
                    }
                }
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
        return bmp;
    }

    private bool TryAcquireFrame(uint timeoutMs, out IDXGIResource resource, out OutduplFrameInfo frameInfo)
    {
        var result = _duplication!.AcquireNextFrame(timeoutMs, out frameInfo, out var res);
        if (result.Success)
        {
            resource = res!;
            return true;
        }
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            resource = null!;
            return false;
        }
        throw new InvalidOperationException($"AcquireNextFrame 失败:{result}(显示模式变化/UAC 切面/被遮挡等)。");
    }

    private void ReleaseDuplicationOnly()
    {
        _duplication?.Dispose();
        _duplication = null;
        // 若设备已死,EnsureSession 会全量重建
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _duplication?.Dispose();
        _staging?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _lastValidFrame?.Dispose();
        _duplication = null;
        _staging = null;
        _context = null;
        _device = null;
        _lastValidFrame = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// GDI BitBlt 回退:CreateDIBSection(顶朝下 BGRA)+ BitBlt(SRCCOPY,不带 CAPTUREBLT 避免指针闪烁),
/// 像素拷入 NativeBitmap 并强制 alpha=0xFF(GDI 的 alpha 位不可靠)。
/// </summary>
internal static class GdiCapture
{
    /// <summary>BitBlt 捕获 + 帧内容自检:全黑时重试一次;仍黑视为锁屏/息屏(合法黑),告警并照常返回。</summary>
    public static NativeBitmap Capture(DisplayInfo display)
    {
        var bmp = CaptureCore(display);
        double ratio = ContentProbe.NonBlackRatio(bmp);
        if (ratio >= 0.001)
        {
            return bmp;
        }
        bmp.Dispose();
        PerfLog.Log("Core", $"BitBlt 疑似黑帧 非黑占比={ratio:P2},重试一次");
        bmp = CaptureCore(display);
        ratio = ContentProbe.NonBlackRatio(bmp);
        if (ratio < 0.001)
        {
            PerfLog.Log("Core", $"BitBlt 帧仍全黑(可能锁屏/息屏,属合法黑),照常返回 非黑占比={ratio:P2}");
        }
        return bmp;
    }

    private static NativeBitmap CaptureCore(DisplayInfo display)
    {
        int w = display.Bounds.Width;
        int h = display.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(display), $"显示器尺寸非法:{w}x{h}");
        }
        var bmp = NativeBitmap.Allocate(w, h);
        nint screenDc = 0;
        nint memDc = 0;
        nint hBitmap = 0;
        nint oldBmp = 0;
        bool success = false;
        try
        {
            screenDc = NativeMethods.GetDC(0);
            if (screenDc == 0)
            {
                throw new InvalidOperationException(NativeMethods.LastError("GetDC"));
            }
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == 0)
            {
                throw new InvalidOperationException(NativeMethods.LastError("CreateCompatibleDC"));
            }

            var bmi = new NativeMethods.BITMAPINFO();
            bmi.bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = 40, // sizeof(BITMAPINFOHEADER),固定
                biWidth = w,
                biHeight = -h, // 负值 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
                biSizeImage = (uint)(w * 4 * h),
            };
            unsafe
            {
                hBitmap = NativeMethods.CreateDIBSection(memDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out var bits, 0, 0);
                if (hBitmap == 0 || bits is null)
                {
                    throw new InvalidOperationException(NativeMethods.LastError("CreateDIBSection"));
                }
                oldBmp = NativeMethods.SelectObject(memDc, hBitmap);
                if (!NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc,
                        display.Bounds.X, display.Bounds.Y, NativeMethods.SRCCOPY))
                {
                    throw new InvalidOperationException(NativeMethods.LastError("BitBlt"));
                }
                NativeMethods.GdiFlush();

                byte* src = (byte*)bits;
                byte* dst = (byte*)bmp.Scan0;
                int rowBytes = w * 4;
                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(
                        src + (long)y * rowBytes,
                        dst + (long)y * bmp.Stride,
                        (ulong)bmp.Stride,
                        (ulong)rowBytes);
                }
                // alpha 置 0xFF:屏幕内容天然不透明,GDI 可能给出任意 alpha
                for (int y = 0; y < h; y++)
                {
                    byte* row = dst + (long)y * bmp.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        row[x * 4 + 3] = 0xFF;
                    }
                }
            }
            success = true;
            return bmp;
        }
        finally
        {
            if (oldBmp != 0)
            {
                NativeMethods.SelectObject(memDc, oldBmp);
            }
            if (hBitmap != 0)
            {
                NativeMethods.DeleteObject(hBitmap);
            }
            if (memDc != 0)
            {
                NativeMethods.DeleteDC(memDc);
            }
            if (screenDc != 0)
            {
                NativeMethods.ReleaseDC(0, screenDc);
            }
            if (!success)
            {
                bmp.Dispose();
            }
        }
    }
}
