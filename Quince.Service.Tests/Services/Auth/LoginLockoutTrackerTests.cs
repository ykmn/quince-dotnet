using Microsoft.Extensions.Logging.Abstractions;
using Quince.Service.Configuration;
using Quince.Service.Services.Auth;
using Xunit;

namespace Quince.Service.Tests.Services.Auth;

public class LoginLockoutTrackerTests
{
    private static LockoutConfig MakeConfig(bool enabled = true, int maxAttempts = 3, int lockoutSeconds = 300,
        int maxLockoutCycles = 2, int ipLockoutSeconds = 3600) => new()
    {
        Enabled = enabled,
        MaxAttempts = maxAttempts,
        LockoutSeconds = lockoutSeconds,
        MaxLockoutCycles = maxLockoutCycles,
        IpLockoutSeconds = ipLockoutSeconds,
    };

    private static LoginLockoutTracker MakeTracker() => new(NullLogger<LoginLockoutTracker>.Instance);

    [Fact]
    public void CheckBlocked_NoPriorAttempts_ReturnsNone()
    {
        var tracker = MakeTracker();
        var result = tracker.CheckBlocked("ivanov", "10.0.0.1");
        Assert.Equal(LoginBlockReason.None, result.Reason);
    }

    [Fact]
    public void RecordFailure_BelowThreshold_DoesNotLockAccount()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(maxAttempts: 3);

        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);

        var result = tracker.CheckBlocked("ivanov", "10.0.0.1");
        Assert.Equal(LoginBlockReason.None, result.Reason);
    }

    [Fact]
    public void RecordFailure_ReachingMaxAttempts_LocksAccount()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(maxAttempts: 3, lockoutSeconds: 300);

        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);

        var result = tracker.CheckBlocked("ivanov", "10.0.0.1");
        Assert.Equal(LoginBlockReason.AccountLocked, result.Reason);
        Assert.True(result.RetryAfter is { } retry && retry > TimeSpan.Zero && retry <= TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void RecordFailure_AccountLockout_IsCaseInsensitiveOnUsername()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(maxAttempts: 2);

        tracker.RecordFailure("Ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("IVANOV", "10.0.0.1", cfg);

        var result = tracker.CheckBlocked("ivanov", "10.0.0.1");
        Assert.Equal(LoginBlockReason.AccountLocked, result.Reason);
    }

    [Fact]
    public void RecordFailure_Disabled_NeverLocksAnything()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(enabled: false, maxAttempts: 1);

        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);

        Assert.Equal(LoginBlockReason.None, tracker.CheckBlocked("ivanov", "10.0.0.1").Reason);
    }

    [Fact]
    public void RecordSuccess_ClearsFailCountForUsername()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(maxAttempts: 3);

        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordSuccess("ivanov", "10.0.0.1");
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);

        // Only 2 consecutive failures since the reset — still below the threshold of 3.
        Assert.Equal(LoginBlockReason.None, tracker.CheckBlocked("ivanov", "10.0.0.1").Reason);
    }

    [Fact]
    public void RecordFailure_RepeatedLockoutsFromSameIp_EscalatesToIpBlock()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(maxAttempts: 2, maxLockoutCycles: 2);

        // First account locks out (2 failures) — one lockout event against the IP so far.
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        Assert.Equal(LoginBlockReason.AccountLocked, tracker.CheckBlocked("ivanov", "10.0.0.1").Reason);

        // A second, different username locked out from the same IP — second lockout event, reaches
        // max_lockout_cycles, so the IP itself should now be blocked regardless of username.
        tracker.RecordFailure("petrov", "10.0.0.1", cfg);
        tracker.RecordFailure("petrov", "10.0.0.1", cfg);

        var result = tracker.CheckBlocked("sidorov", "10.0.0.1");
        Assert.Equal(LoginBlockReason.IpBlocked, result.Reason);
        Assert.True(result.RetryAfter is { } retry && retry > TimeSpan.Zero);
    }

    [Fact]
    public void RecordFailure_LockoutsFromDifferentIps_DoNotCrossContaminate()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(maxAttempts: 2, maxLockoutCycles: 2);

        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);

        tracker.RecordFailure("petrov", "10.0.0.2", cfg);
        tracker.RecordFailure("petrov", "10.0.0.2", cfg);

        // Each IP only accumulated one lockout event — neither should have escalated to an IP block.
        Assert.Equal(LoginBlockReason.AccountLocked, tracker.CheckBlocked("ivanov", "10.0.0.1").Reason);
        Assert.Equal(LoginBlockReason.AccountLocked, tracker.CheckBlocked("petrov", "10.0.0.2").Reason);
    }

    [Fact]
    public async Task CheckBlocked_AfterLockoutExpires_ClearsAutomatically()
    {
        var tracker = MakeTracker();
        var cfg = MakeConfig(maxAttempts: 2, lockoutSeconds: 1);

        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        tracker.RecordFailure("ivanov", "10.0.0.1", cfg);
        Assert.Equal(LoginBlockReason.AccountLocked, tracker.CheckBlocked("ivanov", "10.0.0.1").Reason);

        await Task.Delay(TimeSpan.FromSeconds(1.2));

        Assert.Equal(LoginBlockReason.None, tracker.CheckBlocked("ivanov", "10.0.0.1").Reason);
    }
}
