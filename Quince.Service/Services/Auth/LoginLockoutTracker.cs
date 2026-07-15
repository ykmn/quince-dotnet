using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Services.Auth;

public enum LoginBlockReason { None, AccountLocked, IpBlocked }

public record LoginBlockCheck(LoginBlockReason Reason, TimeSpan? RetryAfter = null);

/// <summary>
/// Tracks consecutive failed login attempts per username and, once the same source IP has caused
/// enough separate lockout events (whether against one repeatedly-guessed username or several
/// different ones), escalates to blocking that IP outright regardless of username. In-memory only —
/// resets on a service restart, same tradeoff <see cref="AuthService"/>'s thread-pool warmup makes
/// elsewhere: the goal is slowing down a live brute-force attempt, not remembering one forever.
/// Every check/record call is a no-op when <see cref="LockoutConfig.Enabled"/> is false, so an
/// existing ldap.yaml without a <c>lockout:</c> section gets no behavior change.
/// </summary>
public class LoginLockoutTracker
{
    private sealed class UserState
    {
        public int FailCount;
        public DateTimeOffset? LockedUntil;
    }

    private sealed class IpState
    {
        public int LockoutEvents;
        public DateTimeOffset? BlockedUntil;
    }

    private readonly ConcurrentDictionary<string, UserState> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IpState> _ips = new();
    private readonly ILogger<LoginLockoutTracker> _logger;

    public LoginLockoutTracker(ILogger<LoginLockoutTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>Called before attempting the real credential check — rejects immediately (no LDAP/BCrypt
    /// work spent) if the username or the source IP is currently locked out. Lazily clears an expired
    /// lock the moment it's observed, rather than needing a background sweep.</summary>
    public LoginBlockCheck CheckBlocked(string username, string ip)
    {
        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(ip) && _ips.TryGetValue(ip, out var ipState))
        {
            lock (ipState)
            {
                if (ipState.BlockedUntil is { } ipUntil)
                {
                    if (now < ipUntil) return new LoginBlockCheck(LoginBlockReason.IpBlocked, ipUntil - now);
                    ipState.BlockedUntil = null;
                    ipState.LockoutEvents = 0;
                }
            }
        }

        if (!string.IsNullOrEmpty(username) && _users.TryGetValue(username, out var userState))
        {
            lock (userState)
            {
                if (userState.LockedUntil is { } until)
                {
                    if (now < until) return new LoginBlockCheck(LoginBlockReason.AccountLocked, until - now);
                    userState.LockedUntil = null;
                    userState.FailCount = 0;
                }
            }
        }

        return new LoginBlockCheck(LoginBlockReason.None);
    }

    /// <summary>Records one failed attempt. Once <see cref="LockoutConfig.MaxAttempts"/> consecutive
    /// failures accumulate for <paramref name="username"/>, locks that username out for
    /// <see cref="LockoutConfig.LockoutSeconds"/> and counts one more "lockout event" against the
    /// source IP — once that count reaches <see cref="LockoutConfig.MaxLockoutCycles"/>, the IP itself
    /// gets blocked for <see cref="LockoutConfig.IpLockoutSeconds"/>, independent of username.</summary>
    public void RecordFailure(string username, string ip, LockoutConfig cfg)
    {
        if (!cfg.Enabled || string.IsNullOrEmpty(username)) return;

        var userState = _users.GetOrAdd(username, _ => new UserState());
        bool justLockedOut;
        lock (userState)
        {
            userState.FailCount++;
            justLockedOut = userState.FailCount >= Math.Max(1, cfg.MaxAttempts);
            if (!justLockedOut) return;
            userState.FailCount = 0;
            userState.LockedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, cfg.LockoutSeconds));
        }

        _logger.LogWarning(
            "Учётная запись «{User}» заблокирована на {Seconds}с после {Max} неудачных попыток входа подряд (IP {Ip})",
            username, cfg.LockoutSeconds, cfg.MaxAttempts, string.IsNullOrEmpty(ip) ? "?" : ip);

        if (string.IsNullOrEmpty(ip)) return;

        var ipState = _ips.GetOrAdd(ip, _ => new IpState());
        int events;
        lock (ipState) { events = ++ipState.LockoutEvents; }

        if (events < Math.Max(1, cfg.MaxLockoutCycles)) return;

        lock (ipState) { ipState.BlockedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, cfg.IpLockoutSeconds)); }
        _logger.LogWarning(
            "IP-адрес {Ip} заблокирован на {Seconds}с — с него подряд зафиксировано {Count} блокировок учётных записей (последняя — «{User}»)",
            ip, cfg.IpLockoutSeconds, events, username);
    }

    /// <summary>Forgives past failures for this username on a successful login — a legitimate login
    /// clears the slate for next time. Deliberately does not touch an active IP block: reaching this
    /// method at all means <see cref="CheckBlocked"/> already found the IP not blocked.</summary>
    public void RecordSuccess(string username, string ip)
    {
        if (!string.IsNullOrEmpty(username) && _users.TryGetValue(username, out var userState))
            lock (userState) { userState.FailCount = 0; userState.LockedUntil = null; }

        if (!string.IsNullOrEmpty(ip) && _ips.TryGetValue(ip, out var ipState))
            lock (ipState) { ipState.LockoutEvents = 0; }
    }
}
