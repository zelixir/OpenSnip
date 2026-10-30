namespace OpenSnip.App.Services;

/// <summary>托管入口时钟:近似"进程启动",用于 PerfLog 全链路打点(§3.4:进程启动→托盘就绪)。</summary>
internal static class AppClock
{
    internal static readonly long StartTickCount = Environment.TickCount64;

    /// <summary>自托管入口起的毫秒数。</summary>
    internal static long ElapsedMs => Environment.TickCount64 - StartTickCount;
}
