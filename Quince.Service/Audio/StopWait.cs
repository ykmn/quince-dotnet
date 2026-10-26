using Microsoft.Extensions.Logging;

namespace Quince.Service.Audio;

/// <summary>Bounded wait for a background task during <c>Stop()</c>. A faulted or cancelled task
/// counts as finished; a task still running when the timeout expires is logged instead of being
/// silently abandoned (the old <c>try { Wait } catch (AggregateException) { }</c> hid both cases).</summary>
internal static class StopWait
{
    /// <returns>true if the task finished (or there was none), false if it was still running at the timeout.</returns>
    public static bool Wait(Task? task, TimeSpan timeout, ILogger log, string what)
    {
        if (task == null) return true;
        try
        {
            if (task.Wait(timeout)) return true;
        }
        catch (AggregateException)
        {
            return true;
        }
        log.LogWarning("{What}: задача не завершилась за {Timeout:F0} с при остановке", what, timeout.TotalSeconds);
        return false;
    }
}
