using System.Runtime.InteropServices;
using System.Text;

namespace OpenSnip.Core;

/// <summary>
/// HDR 色彩修正(契约 §17):HDR 屏的 DD 捕获是 SDR 映射,偏暗;
/// 按 SDR 白电平增益(nits/80,钳位 1.0~4.0)提升 RGB,Alpha 不动。
/// 数学纯函数收敛在本类,表驱动单测不依赖真 HDR 屏。
/// </summary>
internal static unsafe class HdrCorrection
{
    /// <summary>SDR 参考白电平(契约:nits/80)。</summary>
    public const double NitsReference = 80.0;

    /// <summary>nits → 增益(nits/80,钳位 1.0~4.0)。</summary>
    public static double GainFromNits(double nits)
        => Math.Clamp(nits / NitsReference, 1.0, 4.0);

    /// <summary>单通道增益(0-255,四舍五入,钳位 255)。</summary>
    public static byte ApplyToChannel(byte value, double gain)
        => (byte)Math.Round(Math.Clamp(value * gain, 0, 255));

    /// <summary>整幅应用增益(LUT:256 项查表,R/G/B 生效,Alpha 保持)。</summary>
    public static void Apply(NativeBitmap bmp, double gain)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        if (gain <= 1.0 + 0.0001 && gain >= 1.0 - 0.0001)
        {
            return; // 增益 1.0 无需处理
        }
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = ApplyToChannel((byte)v, gain);
        }
        fixed (byte* lutPtr = lut)
        {
            byte* basePtr = (byte*)bmp.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = basePtr + (long)y * bmp.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte* p = row + x * 4;
                    p[0] = lutPtr[p[0]];
                    p[1] = lutPtr[p[1]];
                    p[2] = lutPtr[p[2]];
                    // p[3](Alpha)保持
                }
            }
        }
    }
}

/// <summary>
/// HDR SDR 白电平查询(DisplayConfig):
/// 优先 GET_ADVANCED_COLOR_INFO_2(含 DesiredACIIdeallyBrightInNits,SDK 26100 头文件尚未收录该字段,
/// 以扩展缓冲自声明,OS 新版可填充);不可用回落 GET_SDR_WHITE_LEVEL
/// (SDRWhiteLevel 单位 = nits×1000/80,注释见 wingdi.h 3276 行起),换算后与契约公式严格等价。
/// <para>结构布局以 wingdi.h 3062/3167/3212/3276 行定义为依据;PATH_INFO(96B)/MODE_INFO(72B)
/// 以字节缓冲 + 头文件字段序偏移访问,避免手转写大结构。</para>
/// </summary>
internal static class HdrWhiteLevel
{
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const uint DisplayConfigDeviceInfoGetAdvancedColorInfo = 9;
    private const uint DisplayConfigDeviceInfoGetSdrWhiteLevel = 11;
    private const uint DisplayConfigDeviceInfoGetAdvancedColorInfo2 = 15;
    private const uint QdcOnlyActivePaths = 2;
    private const int PathInfoSize = 96;   // header 20 + sourceInfo 20 + targetInfo 52 + flags 4
    private const int ModeInfoSize = 72;   // infoType 4 + id 4 + LUID 8 + union(TARGET_MODE 48)
    private const int SourceNameSize = 84; // header 20 + WCHAR[32]
    private const int AdvancedColorInfo2Size = 40; // header 20 + value 4 + colorEncoding 4 + bitsPerColorChannel 4 + activeColorMode 4 + DesiredACIIdeallyBrightInNits 4

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint numPathArrayElements, byte[] pathArray,
        ref uint numModeInfoArrayElements, byte[] modeInfoArray, out uint currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(byte[] requestPacket);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (double Gain, DateTime Expires)> GainCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 取指定 GDI 设备名(如 \.DISPLAY1)的 HDR SDR 白电平增益(钳位 1.0~4.0)。
    /// 查询失败/非 HDR/无数据 → 返回 1.0(不修正)。带 3s TTL 缓存。
    /// </summary>
    public static double GetGain(string gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName))
        {
            return 1.0;
        }
        lock (Gate)
        {
            if (GainCache.TryGetValue(gdiDeviceName, out var hit) && DateTime.UtcNow < hit.Expires)
            {
                return hit.Gain;
            }
        }
        double gain = 1.0;
        try
        {
            gain = HdrCorrection.GainFromNits(QueryNits(gdiDeviceName));
        }
        catch
        {
            gain = 1.0; // 任何 DisplayConfig 异常都静默回落"不修正"
        }
        lock (Gate)
        {
            GainCache[gdiDeviceName] = (gain, DateTime.UtcNow + CacheTtl);
        }
        return gain;
    }

    /// <summary>清空白电平缓存(显示器热插拔/设置变化时可调用)。</summary>
    public static void ClearCache()
    {
        lock (Gate)
        {
            GainCache.Clear();
        }
    }

    /// <summary>查询 SDR 白电平(nits)。两段式:ADVANCED_COLOR_INFO_2 的 DesiredACIIdeallyBrightInNits → 回落 SDR_WHITE_LEVEL。</summary>
    private static double QueryNits(string gdiDeviceName)
    {
        foreach (var (adapterId, sourceId) in EnumerateActiveSources(gdiDeviceName))
        {
            // 1) GET_ADVANCED_COLOR_INFO_2(=15):扩展自声明缓冲,新版 OS 填 DesiredACIIdeallyBrightInNits(@36)
            var pkt2 = new byte[AdvancedColorInfo2Size];
            WriteHeader(pkt2, DisplayConfigDeviceInfoGetAdvancedColorInfo2, adapterId, sourceId);
            if (DisplayConfigGetDeviceInfo(pkt2) == 0)
            {
                uint nits = BitConverter.ToUInt32(pkt2, 36);
                if (nits > 0)
                {
                    return nits; // DesiredACIIdeallyBrightInNits(原生单位即 nits)
                }
            }

            // 2) 回落 GET_SDR_WHITE_LEVEL(=11):SDRWhiteLevel = nits × 1000 / 80
            var pkt1 = new byte[24];
            WriteHeader(pkt1, DisplayConfigDeviceInfoGetSdrWhiteLevel, adapterId, sourceId);
            if (DisplayConfigGetDeviceInfo(pkt1) == 0)
            {
                uint sdr = BitConverter.ToUInt32(pkt1, 20);
                if (sdr > 0)
                {
                    return sdr * HdrCorrection.NitsReference / 1000.0;
                }
            }
        }
        return 0; // 未找到匹配源/无数据:调用方换算后钳到 1.0(不修正)
    }

    /// <summary>枚举活动路径中 GDI 设备名匹配的活动源 (adapterId, sourceId)。</summary>
    private static IEnumerable<(ulong AdapterId, uint SourceId)> EnumerateActiveSources(string gdiDeviceName)
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0
            || pathCount == 0)
        {
            yield break;
        }
        var paths = new byte[pathCount * PathInfoSize];
        var modes = new byte[modeCount * ModeInfoSize];
        uint nPath = pathCount;
        uint nMode = modeCount;
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref nPath, paths, ref nMode, modes, out _) != 0)
        {
            yield break;
        }
        for (uint i = 0; i < nPath; i++)
        {
            long baseOffset = i * PathInfoSize;
            // PATH_INFO:header(20) → sourceInfo:adapterId(@+20,8B)/id(@+28,4B)
            ulong adapterId = BitConverter.ToUInt64(paths, (int)(baseOffset + 20));
            uint sourceId = BitConverter.ToUInt32(paths, (int)(baseOffset + 28));

            var namePkt = new byte[SourceNameSize];
            WriteHeader(namePkt, DisplayConfigDeviceInfoGetSourceName, adapterId, sourceId);
            if (DisplayConfigGetDeviceInfo(namePkt) != 0)
            {
                continue;
            }
            var name = Encoding.Unicode.GetString(namePkt, 20, 64).TrimEnd('\0');
            if (string.Equals(name, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
            {
                yield return (adapterId, sourceId);
            }
        }
    }

    private static void WriteHeader(byte[] packet, uint type, ulong adapterId, uint id)
    {
        BitConverter.GetBytes(type).CopyTo(packet, 0);
        BitConverter.GetBytes((uint)packet.Length).CopyTo(packet, 4);
        BitConverter.GetBytes(adapterId).CopyTo(packet, 8);
        BitConverter.GetBytes(id).CopyTo(packet, 16);
    }
}
