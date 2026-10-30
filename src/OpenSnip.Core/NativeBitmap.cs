using System.Runtime.InteropServices;

namespace OpenSnip.Core;

/// <summary>
/// 驻非托管内存的 BGRA32 非预乘位图(红线 §0.4:大位图禁止 byte[] 承载)。
/// <para>内存来自 <see cref="NativeMemory.AlignedAlloc"/>(64 字节对齐);
/// 行距(Stride)= 4*Width 向上对齐到 32 字节,便于 SIMD 行级操作。</para>
/// </summary>
public sealed class NativeBitmap : IDisposable
{
    private IntPtr _scan0;
    private bool _disposed;

    private NativeBitmap(int width, int height, int stride, IntPtr scan0)
    {
        Width = width;
        Height = height;
        _scan0 = scan0;
        Stride = stride;
    }

    /// <summary>像素宽(物理 px)。</summary>
    public int Width { get; }

    /// <summary>像素高(物理 px)。</summary>
    public int Height { get; }

    /// <summary>行距字节数:4*Width 向上对齐到 32 字节。</summary>
    public int Stride { get; }

    /// <summary>像素起始地址(AlignedAlloc 64)。释放后访问抛 <see cref="ObjectDisposedException"/>。</summary>
    public IntPtr Scan0
    {
        get
        {
            ThrowIfDisposed();
            return _scan0;
        }
    }

    /// <summary>按 32 字节对齐计算行距。</summary>
    public static int ComputeStride(int width) => (width * 4 + 31) & ~31;

    /// <summary>分配一块清零的 BGRA32 位图。</summary>
    public static NativeBitmap Allocate(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int stride = ComputeStride(width);
        long bytes = checked((long)stride * height);
        nuint size = (nuint)bytes;
        nint p;
        unsafe
        {
            p = (nint)NativeMemory.AlignedAlloc(size, 64);
            new Span<byte>((void*)p, checked((int)bytes)).Clear();
        }
        return new NativeBitmap(width, height, stride, new IntPtr(p));
    }

    /// <summary>
    /// 从源位图左上角 (srcX, srcY) 起拷贝"本位图尺寸"的区域(按契约语义:目标整幅被填充)。
    /// 用于选区确认时从冻结的全屏位图裁剪。
    /// </summary>
    public unsafe void CopyRegionFrom(NativeBitmap src, int srcX, int srcY)
    {
        ArgumentNullException.ThrowIfNull(src);
        ThrowIfDisposed();
        if (srcX < 0 || srcY < 0 || srcX + Width > src.Width || srcY + Height > src.Height)
            throw new ArgumentOutOfRangeException(
                nameof(srcX),
                $"复制区域 ({srcX},{srcY}) + {Width}x{Height} 超出源位图 {src.Width}x{src.Height}。");
        byte* dstBase = (byte*)Scan0;
        byte* srcBase = (byte*)src.Scan0;
        ulong rowBytes = (ulong)((long)Width * 4);
        for (int y = 0; y < Height; y++)
        {
            byte* srcRow = srcBase + (long)(srcY + y) * src.Stride + (long)srcX * 4;
            byte* dstRow = dstBase + (long)y * Stride;
            Buffer.MemoryCopy(srcRow, dstRow, (ulong)Stride, rowBytes);
        }
    }

    /// <summary>释放非托管内存;可重入(防双释放)。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_scan0 != IntPtr.Zero)
        {
            unsafe
            {
                NativeMemory.AlignedFree((void*)_scan0);
            }
            _scan0 = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~NativeBitmap() => Dispose(); // 终结器兜底防泄漏;_disposed 保证不双释放

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
