using System.Runtime.InteropServices;

namespace OpenSnip.Core;

/// <summary>
/// 剪贴板写图像:一次 OpenClipboard 写双格式 CF_DIB + 注册格式 "PNG"。
/// 调用线程需已 OLE 初始化(契约:App 侧保证 STA)。
/// </summary>
public static class ClipboardWriter
{
    private const int OpenRetry = 5;
    private const int OpenRetryDelayMs = 10;

    /// <summary>成功写入返回 true;无法打开剪贴板/分配失败返回 false(打 PerfLog)。</summary>
    public static unsafe bool PutImage(NativeBitmap bmp)
    {
        ArgumentNullException.ThrowIfNull(bmp);
        byte[] dib = BitmapConversion.GetDibBytes(bmp);
        byte[] png = BitmapConversion.EncodePng(bmp);
        uint pngFormat = NativeMethods.RegisterClipboardFormatW("PNG");
        if (pngFormat == 0)
        {
            PerfLog.Log("Core", "RegisterClipboardFormatW(PNG格式) 失败,仅写 CF_DIB:" + NativeMethods.LastError("RegisterClipboardFormatW"));
        }

        for (int attempt = 1; attempt <= OpenRetry; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                if (attempt == OpenRetry)
                {
                    PerfLog.Log("Core", $"PutImage:OpenClipboard 重试 {OpenRetry} 次失败:{NativeMethods.LastError("OpenClipboard")}");
                    return false;
                }
                Thread.Sleep(OpenRetryDelayMs);
                continue;
            }

            bool ok;
            try
            {
                if (!NativeMethods.EmptyClipboard())
                {
                    PerfLog.Log("Core", "EmptyClipboard 失败:" + NativeMethods.LastError("EmptyClipboard"));
                    return false;
                }
                bool dibOk = SetData(NativeMethods.CF_DIB, dib);
                bool pngOk = pngFormat == 0 || SetData(pngFormat, png);
                ok = dibOk && pngOk;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            if (!ok)
            {
                PerfLog.Log("Core", "PutImage:部分格式写入失败(剪贴板可能只含部分内容)。");
            }
            return ok;
        }
        return false;
    }

    /// <summary>GlobalAlloc → 锁定 → 拷入 → SetClipboardData。所有权成功即移交系统;失败自行 GlobalFree。</summary>
    private static bool SetData(uint format, byte[] bytes)
    {
        nint hMem = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (hMem == IntPtr.Zero)
        {
            PerfLog.Log("Core", $"GlobalAlloc({bytes.Length}B) 失败:" + NativeMethods.LastError("GlobalAlloc"));
            return false;
        }
        nint p = NativeMethods.GlobalLock(hMem);
        if (p == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(hMem);
            PerfLog.Log("Core", "GlobalLock 失败:" + NativeMethods.LastError("GlobalLock"));
            return false;
        }
        try
        {
            Marshal.Copy(bytes, 0, p, bytes.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(hMem);
        }
        if (NativeMethods.SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(hMem); // 失败所有权仍在调用方
            PerfLog.Log("Core", $"SetClipboardData(format={format}) 失败:" + NativeMethods.LastError("SetClipboardData"));
            return false;
        }
        return true;
    }
}
