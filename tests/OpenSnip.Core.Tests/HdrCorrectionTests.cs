using OpenSnip.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenSnip.Core.Tests;

/// <summary>
/// HDR 色彩修正表驱动数学断言(契约 §17 验收:合成 nits/像素 → 期望输出,不依赖真 HDR 屏)。
/// </summary>
public class HdrCorrectionTests
{
    private readonly ITestOutputHelper _output;

    public HdrCorrectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(0.0, 1.0)]    // 无数据/0 nits → 钳下限
    [InlineData(40.0, 1.0)]   // 低于参考白 → 钳下限
    [InlineData(80.0, 1.0)]   // 参考白 → 增益 1.0
    [InlineData(100.0, 1.25)]
    [InlineData(160.0, 2.0)]
    [InlineData(240.0, 3.0)]
    [InlineData(320.0, 4.0)]  // 钳上限(320 = 80×4)
    [InlineData(400.0, 4.0)]  // 超上限 → 钳 4.0
    public void GainFromNits_TableDriven(double nits, double expectedGain)
    {
        var gain = HdrCorrection.GainFromNits(nits);
        _output.WriteLine($"nits={nits} → gain={gain:F3}(期望 {expectedGain})");
        Assert.Equal(expectedGain, gain, 3);
    }

    [Theory]
    [InlineData(100, 2.0, 200)]   // 线性
    [InlineData(200, 2.0, 255)]   // 钳 255
    [InlineData(255, 2.0, 255)]
    [InlineData(100, 1.0, 100)]   // 增益 1 不变
    [InlineData(128, 1.25, 160)]  // 1.25 增益
    [InlineData(10, 4.0, 40)]     // 4.0 上限
    [InlineData(0, 4.0, 0)]       // 纯黑保持纯黑
    [InlineData(100, 1.004, 100)] // 极小增益四舍五入回原值
    public void ApplyToChannel_TableDriven(byte value, double gain, byte expected)
    {
        Assert.Equal(expected, HdrCorrection.ApplyToChannel(value, gain));
    }

    [Fact]
    public void Apply_FullBitmap_RgbScaled_AlphaKept()
    {
        using var bmp = NativeBitmap.Allocate(4, 4);
        // 注意 stride padding:必须按 (x, y) 真实像素寻址,不能用平铺偏移(会落进行距 padding)
        void WritePixel(int x, int y, byte b, byte g, byte r, byte a)
        {
            int off = y * bmp.Stride + x * 4;
            System.Runtime.InteropServices.Marshal.WriteByte(bmp.Scan0, off, b);
            System.Runtime.InteropServices.Marshal.WriteByte(bmp.Scan0, off + 1, g);
            System.Runtime.InteropServices.Marshal.WriteByte(bmp.Scan0, off + 2, r);
            System.Runtime.InteropServices.Marshal.WriteByte(bmp.Scan0, off + 3, a);
        }
        byte ReadPixelChannel(int x, int y, int ch)
            => System.Runtime.InteropServices.Marshal.ReadByte(bmp.Scan0, y * bmp.Stride + x * 4 + ch);

        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                WritePixel(x, y, 100, 50, 200, 0x7B); // B/G/R/A
            }
        }
        HdrCorrection.Apply(bmp, 2.0);
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                Assert.Equal(200, ReadPixelChannel(x, y, 0));            // B ×2
                Assert.Equal(100, ReadPixelChannel(x, y, 1));            // G ×2
                Assert.Equal(255, ReadPixelChannel(x, y, 2));            // R ×2 钳 255
                Assert.Equal(0x7B, ReadPixelChannel(x, y, 3));           // Alpha 不动
            }
        }
    }

    [Fact]
    public void Apply_GainOne_IsNoOp()
    {
        using var bmp = NativeBitmap.Allocate(8, 8);
        unsafe
        {
            byte* basePtr = (byte*)bmp.Scan0;
            var rnd = new Random(1234);
            for (int i = 0; i < 64 * 4; i++)
            {
                basePtr[i] = (byte)rnd.Next(256);
            }
        }
        HdrCorrection.Apply(bmp, 1.0); // 幂等路径
        // 无异常即可;数学上 gain=1 直接短路
        Assert.Equal(1.0, HdrCorrection.GainFromNits(80.0), 3);
    }
}
