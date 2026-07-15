using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Services.Auth;

/// <summary>User-visible authentication error (wrong password, unknown user, LDAP config problem,
/// lockout in effect, etc.) — caught at the login endpoint and shown to the caller as-is.</summary>
public class AuthException : Exception
{
    /// <summary>HTTP status the login endpoint should respond with — 401 for ordinary auth failures,
    /// 429 for a lockout/rate-limit rejection (see <see cref="LoginLockoutTracker"/>).</summary>
    public int StatusCode { get; }

    public AuthException(string message, int statusCode = 401) : base(message)
    {
        StatusCode = statusCode;
    }
}

public record AuthResult(string Username, bool IsAdmin, string AuthType, string Domain = "");

/// <summary>Ports apricot2's app/auth.py: config/ldap.yaml presence is the auth on/off switch,
/// config/users.yaml holds local (BCrypt) accounts, LDAP/AD is delegated to
/// <see cref="LdapAuthenticator"/>. Sessions are an in-memory token -> record map persisted to
/// config/sessions.yaml so a login survives an app/service restart.</summary>
public class AuthService
{
    public const string CookieName = "quince_session";

    private readonly YamlConfigLoader _loader;
    private readonly LdapAuthenticator _ldap;
    private readonly AppSettingsService _appSettings;
    private readonly LoginLockoutTracker _lockout;
    private readonly ILogger<AuthService> _logger;
    private readonly string _configDir;

    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new();
    private readonly object _sessionsFileLock = new();

    private readonly object _ephemeralLock = new();
    private LocalUserEntry? _ephemeralAdmin;

    public AuthService(YamlConfigLoader loader, LdapAuthenticator ldap, AppSettingsService appSettings,
        LoginLockoutTracker lockout, ILogger<AuthService> logger, IConfiguration configuration)
    {
        _loader = loader;
        _ldap = ldap;
        _appSettings = appSettings;
        _lockout = lockout;
        _logger = logger;
        _configDir = PathResolver.Resolve(configuration["ConfigDir"], "config");
        LoadPersistedSessions();
    }

    /// <summary>False (open app, no login) when config/ldap.yaml is absent or configures neither
    /// Local nor Ldap — same default as before this feature existed.</summary>
    public bool AuthRequired
    {
        get
        {
            var cfg = _loader.LoadLdapConfig(_configDir);
            return cfg.Present && (cfg.Local || cfg.Ldap);
        }
    }

    /// <summary>Priority: local accounts first, then LDAP — same as apricot2. Returns null only when
    /// auth isn't configured at all (caller should allow access); throws <see cref="AuthException"/>
    /// with a user-facing message on any definitive failure — including a lockout rejection
    /// (<see cref="AuthException.StatusCode"/> 429), checked before spending any LDAP/BCrypt work.</summary>
    public AuthResult? Authenticate(string username, string password, string ip)
    {
        var cfg = _loader.LoadLdapConfig(_configDir);
        if (!cfg.Present) return null;

        var block = _lockout.CheckBlocked(username, ip);
        if (block.Reason == LoginBlockReason.IpBlocked)
            throw new AuthException($"Слишком много неудачных попыток входа с этого IP-адреса. Повторите через {FormatRetryAfter(block.RetryAfter)}.", statusCode: 429);
        if (block.Reason == LoginBlockReason.AccountLocked)
            throw new AuthException($"Учётная запись «{username}» временно заблокирована из-за неудачных попыток входа. Повторите через {FormatRetryAfter(block.RetryAfter)}.", statusCode: 429);

        try
        {
            var result = AuthenticateCore(username, password, cfg);
            if (result != null) _lockout.RecordSuccess(username, ip);
            return result;
        }
        catch (AuthException)
        {
            _lockout.RecordFailure(username, ip, cfg.Lockout);
            throw;
        }
    }

    private static string FormatRetryAfter(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } ts || ts <= TimeSpan.Zero) return "некоторое время";
        return ts.TotalMinutes >= 1 ? $"{Math.Ceiling(ts.TotalMinutes)} мин." : $"{Math.Ceiling(ts.TotalSeconds)} с.";
    }

    private AuthResult? AuthenticateCore(string username, string password, LdapConfig cfg)
    {
        if (cfg.Local)
        {
            var localUser = LoadLocalUsers().FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            if (localUser != null)
            {
                if (PasswordHasher.Verify(password, localUser.PasswordHash))
                    return new AuthResult(username, localUser.IsAdmin, "local");
                // Username matched locally but the password didn't — don't fall through to LDAP for
                // the same username (same rule apricot2 applies).
                throw new AuthException("Неверный пароль.");
            }
        }

        if (cfg.Ldap)
        {
            var outcome = _ldap.Authenticate(username, password, cfg, id => ResolveSecret(id));
            if (outcome.Tag == LdapOutcomeTag.Success)
                return new AuthResult(outcome.Username, outcome.IsAdmin, "ldap", outcome.Domain);
            throw new AuthException(outcome.Message);
        }

        throw new AuthException($"Пользователь «{username}» не найден. Проверьте имя пользователя или обратитесь к администратору.");
    }

    private List<LocalUserEntry> LoadLocalUsers()
    {
        var users = _loader.LoadUsers(_configDir).Users;
        if (users.Count > 0) return users;

        lock (_ephemeralLock)
        {
            if (_ephemeralAdmin == null)
            {
                var tempPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                _ephemeralAdmin = new LocalUserEntry
                {
                    Username = "admin",
                    PasswordHash = PasswordHasher.Hash(tempPassword),
                    IsAdmin = true,
                };
                _logger.LogWarning(
                    "config/users.yaml не найден. Временные учётные данные — логин: admin  пароль: {Password}", tempPassword);
                _logger.LogWarning("Создайте config/users.yaml для постоянных учётных данных.");
            }
            return new List<LocalUserEntry> { _ephemeralAdmin };
        }
    }

    private SecretEntry? ResolveSecret(int id) =>
        _loader.LoadSecrets(_configDir).Authorization.FirstOrDefault(s => s.Id == id);

    // ── Sessions ──────────────────────────────────────────────────────────────

    public string CreateSession(string username, bool isAdmin, string authType, string domain, string ip)
    {
        var token = GenerateToken();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ttl = Math.Max(60, _appSettings.Current.AuthSessionTtlSeconds);
        _sessions[token] = new SessionRecord
        {
            Username = username,
            IsAdmin = isAdmin,
            AuthType = authType,
            Domain = domain,
            Ip = ip,
            CreatedAt = now,
            Expires = now + ttl,
        };
        SavePersistedSessions();
        return token;
    }

    public SessionRecord? GetSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > session.Expires)
        {
            _sessions.TryRemove(token, out _);
            SavePersistedSessions();
            return null;
        }
        return session;
    }

    public void DeleteSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        if (_sessions.TryRemove(token, out _))
            SavePersistedSessions();
    }

    private void LoadPersistedSessions()
    {
        var file = _loader.LoadSessions(_configDir);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (token, session) in file.Sessions)
            if (session.Expires > now)
                _sessions[token] = session;
    }

    private void SavePersistedSessions()
    {
        lock (_sessionsFileLock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var key in _sessions.Keys.Where(k => _sessions.TryGetValue(k, out var s) && s.Expires <= now))
                _sessions.TryRemove(key, out _);

            _loader.SaveSessions(_configDir, new SessionsFile { Sessions = new Dictionary<string, SessionRecord>(_sessions) });
        }
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
