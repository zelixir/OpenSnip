using OpenSnip.Core;

namespace OpenSnip.App.Services;

/// <summary>
/// §8.4 测试底图:8 等宽垂直色带(左→右:白 FFFFFF / 黄 FFFF00 / 青 00FFFF / 绿 00FF00 /
/// 品红 FF00FF / 红 FF0000 / 蓝 0000FF / 黑 000000)。像素期望色可由 x 推导:band = floor(x/width*8)。
/// 仅 --test-rpc 模式使用(隐藏桌面 DD/BitBlt 均不可用,实测裁决见 §8.4);
/// E2E 据此把断言升级为像素级(shot 颜色命中色带表 / confirm 裁剪区域逐点采样一致)。
/// </summary>
public static class TestBaseImage
{
    /// <summary>色带固定色表(左→右,BGR 字节序,与 BGRA32 布局一致)。</summary>
    public static readonly (byte B, byte G, byte R)[] Bands =
    {
        (0xFF, 0xFF, 0xFF), // 0 白 FFFFFF
        (0x00, 0xFF, 0xFF), // 1 黄 FFFF00
        (0xFF, 0xFF, 0x00), // 2 青 00FFFF
        (0x00, 0xFF, 0x00), // 3 绿 00FF00
        (0xFF, 0x00, 0xFF), // 4 品红 FF00FF
        (0x00, 0x00, 0xFF), // 5 红 FF0000
        (0xFF, 0x00, 0x00), // 6 蓝 0000FF
        (0x00, 0x00, 0x00), // 7 黑 000000
    };

    /// <summary>色带数量。</summary>
    public const int BandCount = 8;

    /// <summary>给定色带内局部 x 与底图宽,返回期望 RGB——E2E 像素断言用(与填充公式严格同构)。</summary>
    public static (byte R, byte G, byte B) ExpectedRgb(int x, int width)
    {
        int band = (int)((long)x * BandCount / width);
        if (band >= BandCount) band = BandCount - 1;
        var (b, g, r) = Bands[band];
        return (r, g, b);
    }

    /// <summary>按 DisplayInfo.Bounds 生成整块底图(BGRA32 非预乘,alpha=0xFF)。</summary>
    public static NativeBitmap Create(PxRect bounds)
    {
        var bmp = NativeBitmap.Allocate(bounds.Width, bounds.Height);
        unsafe
        {
            byte* basePtr = (byte*)bmp.Scan0;
            for (int band = 0; band < BandCount; band++)
            {
                // 列区间与 ExpectedRgb 的 floor(x*N/W) 公式严格同构
                int x0 = (int)((long)bounds.Width * band / BandCount);
                int x1 = (int)((long)bounds.Width * (band + 1) / BandCount);
                if (x1 <= x0)
                {
                    x1 = Math.Min(x0 + 1, bounds.Width); // 超窄底图兜底
                }
                var (b, g, r) = Bands[band];
                for (int y = 0; y < bounds.Height; y++)
                {
                    byte* row = basePtr + (long)y * bmp.Stride;
                    for (int x = x0; x < x1; x++)
                    {
                        long o = (long)x * 4;
                        row[o] = b;
                        row[o + 1] = g;
                        row[o + 2] = r;
                        row[o + 3] = 0xFF;
                    }
                }
            }
        }
        return bmp;
    }
}
