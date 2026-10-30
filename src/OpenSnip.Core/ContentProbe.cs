namespace OpenSnip.Core;

/// <summary>
/// 帧内容自检:均匀采样统计"非黑像素"占比。
/// 用于防御驱动黑帧 / duplication 空表面帧(<see cref="ScreenCaptureService"/> 与 BitBlt 回退共用)。
/// </summary>
internal static unsafe class ContentProbe
{
    /// <summary>
    /// 返回 [0,1] 的非黑像素采样占比。非黑判定:B/G/R 任一通道 &gt; threshold(alpha 不参与,壁纸可能为纯黑)。
    /// </summary>
    public static double NonBlackRatio(NativeBitmap bmp, int samples = 4096, byte threshold = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        long total = (long)bmp.Width * bmp.Height;
        long nonBlack = 0;
        byte* basePtr = (byte*)bmp.Scan0;
        for (int i = 0; i < samples; i++)
        {
            long idx = (long)i * total / samples;
            int y = (int)(idx / bmp.Width);
            int x = (int)(idx % bmp.Width);
            byte* p = basePtr + (long)y * bmp.Stride + x * 4;
            if (p[0] > threshold || p[1] > threshold || p[2] > threshold)
            {
                nonBlack++;
            }
        }
        return (double)nonBlack / samples;
    }
}
