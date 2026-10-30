// 选区结果(SPEC §2.8 契约冻结区,签名不得改动)
using OpenSnip.Core;

namespace OpenSnip.Overlay;

/// <summary>遮罩选区的最终结果:由冻结位图裁出的区域。</summary>
public sealed class SnipResult
{
    /// <summary>所选区域的裁剪(物理 px,非托管内存,BGRA32 非预乘)。</summary>
    public NativeBitmap Image { get; }

    /// <summary>选区的虚拟桌面物理坐标。</summary>
    public PxRect PhysicalRect { get; }

    /// <summary>选区主判定所在屏(选区中心所在屏;无则取第一个与选区相交的屏;再无则第 0 屏)。</summary>
    public DisplayInfo Display { get; }

    public SnipResult(NativeBitmap image, PxRect physicalRect, DisplayInfo display)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        PhysicalRect = physicalRect;
        Display = display;
    }
}
