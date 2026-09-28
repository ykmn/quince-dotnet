using Quince.Service.Services;
using Xunit;

namespace Quince.Service.Tests.Services;

public class MonitoringDiskCacheTests
{
    [Fact]
    public async Task RefreshKeepsLastMeasurement_WhileReplacementIsPending()
    {
        var now = DateTimeOffset.UtcNow;
        using var gate = new ManualResetEventSlim(false);
        var calls = 0;
        var cache = new MonitoringDiskCache((path, ct) =>
        {
            if (Interlocked.Increment(ref calls) > 1) gate.Wait(TimeSpan.FromSeconds(5));
            return new FolderUsage(100, now);
        });
        try
        {
            cache.Refresh(new[] { "folder" }, now, default);
            FolderUsage? result = null;
            for (var i = 0; i < 100 && result == null; i++)
            {
                await Task.Delay(10);
                result = cache.Refresh(new[] { "folder" }, now, default)["folder"];
            }
            Assert.NotNull(result);
            Assert.Equal(100, cache.Refresh(new[] { "folder" }, now.AddMinutes(5), default)["folder"]?.Bytes);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task HungScansAreNotMultipliedByRefreshOrConfigurationChanges()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new CountdownEvent(2);
        var calls = 0;
        var cache = new MonitoringDiskCache((path, ct) =>
        {
            Interlocked.Increment(ref calls);
            started.Signal();
            gate.Wait(TimeSpan.FromSeconds(5));
            return null;
        });
        try
        {
            var now = DateTimeOffset.UtcNow;
            cache.Refresh(new[] { "one", "two", "three" }, now, default);
            Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(3))));
            cache.Refresh(new[] { "one", "two", "three" }, now.AddHours(1), default);
            cache.Refresh(new[] { "four" }, now.AddHours(2), default);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally { gate.Set(); }
    }
}
