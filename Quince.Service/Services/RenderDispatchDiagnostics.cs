using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Quince.Service.Services;

/// <summary>
/// Diagnostic helper for the recurring "level indicators freeze for a few seconds" investigation
/// (see docs/HISTORY.md #36/#52). Distinguishes two independent stall mechanisms that both present
/// the same way on screen:
/// <list type="bullet">
/// <item><b>Queue lag</b> (<see cref="WarnIfQueueLagged"/>): time between requesting a dispatch
/// (<c>InvokeAsync</c>) and it actually starting to run — a slow pool/dispatcher, not a slow
/// render.</item>
/// <item><b>Work duration</b> (<see cref="WarnIfSlow"/>): how long the dispatched work itself took
/// (e.g. a JS interop round-trip) — an individually slow operation, not backlog.</item>
/// </list>
/// Temporary/ongoing instrumentation, not user-facing — logged at Warning so it's visible in the
/// normal file log without needing Debug-level verbosity turned on.
/// </summary>
public static class RenderDispatchDiagnostics
{
    private static readonly TimeSpan WarnThreshold = TimeSpan.FromMilliseconds(300);

    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    public static void WarnIfQueueLagged(ILogger log, string channelName, string what, long queuedAt)
    {
        var lag = Stopwatch.GetElapsedTime(queuedAt);
        if (lag >= WarnThreshold)
            log.LogWarning(
                "Задержка в очереди диспетчера UI: {What}, канал '{Channel}' — {LagMs:F0}мс в очереди (ThreadPool: {Pending} задач в очереди, {Busy} рабочих потоков занято)",
                what, channelName, lag.TotalMilliseconds, ThreadPool.PendingWorkItemCount, BusyThreadCount());
    }

    public static void WarnIfSlow(ILogger log, string channelName, string what, long startedAt)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        if (elapsed >= WarnThreshold)
            log.LogWarning("Медленное выполнение: {What}, канал '{Channel}' — {ElapsedMs:F0}мс", what, channelName, elapsed.TotalMilliseconds);
    }

    private static int BusyThreadCount()
    {
        // GetAvailableThreads is relative to GetMaxThreads (the pool's ceiling), not GetMinThreads
        // (the warm floor raised to 256 in Program.cs) — subtracting from Max is what actually
        // yields "how many worker threads are currently busy" right now.
        ThreadPool.GetAvailableThreads(out var availableWorker, out _);
        ThreadPool.GetMaxThreads(out var maxWorker, out _);
        return Math.Max(0, maxWorker - availableWorker);
    }
}
