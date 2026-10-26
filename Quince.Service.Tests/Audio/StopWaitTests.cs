using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class StopWaitTests
{
    [Fact]
    public void NullAndCompletedTasks_ReportFinished()
    {
        Assert.True(StopWait.Wait(null, TimeSpan.FromMilliseconds(10), NullLogger.Instance, "x"));
        Assert.True(StopWait.Wait(Task.CompletedTask, TimeSpan.FromMilliseconds(10), NullLogger.Instance, "x"));
    }

    [Fact]
    public void FaultedTask_CountsAsFinished() =>
        Assert.True(StopWait.Wait(Task.FromException(new InvalidOperationException()),
            TimeSpan.FromMilliseconds(10), NullLogger.Instance, "x"));

    [Fact]
    public void StillRunningTask_ReportsTimeout()
    {
        var tcs = new TaskCompletionSource();
        Assert.False(StopWait.Wait(tcs.Task, TimeSpan.FromMilliseconds(30), NullLogger.Instance, "x"));
        tcs.SetResult();
    }
}
