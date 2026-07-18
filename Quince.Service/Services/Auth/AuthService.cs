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

    /// <summary>What actually went wrong, for the server log only — may be more specific than
    /// <see cref="Exception.Message"/> (e.g. "unknown username" vs "wrong password"), since the
    /// message shown to the caller is deliberately generic to avoid letting a failed login reveal
    /// whether the username exists.</summary>
    public string LogDetail { get; }

    public AuthException(string message, int statusCode = 401, string? logDetail = null) : base(message)
    {
        StatusCode = statusCode;
        LogDetail = logDetail ?? message;
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

    // ldap.yaml is re-checked on every incoming request (see AuthRequired / Program.cs middleware),
    // so cache the parsed config and only re-read+re-parse the file when its mtime changes — a plain
    // File.Exists/GetLastWriteTimeUtc stat is far cheaper than a full read+YAML-deserialize per request.
    private readonly object _ldapConfigLock = new();
    private DateTime? _ldapConfigMtime;
    private LdapConfig? _ldapConfigCache;
    private bool _ldapConfigCacheValid;

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
            var cfg = GetLdapConfig();
            return cfg.Present && (cfg.Local || cfg.Ldap);
        }
    }

    /// <summary>Cached wrapper around <see cref="YamlConfigLoader.LoadLdapConfig"/> — this middleware
    /// checks <see cref="AuthRequired"/> on every incoming request, so re-reading and re-parsing
    /// ldap.yaml from disk that often would be wasted work. Re-parses only when the file's mtime
    /// changes (still lets an admin hand-edit ldap.yaml and have it take effect without a restart).</summary>
    private LdapConfig GetLdapConfig()
    {
        var path = Path.Combine(_configDir, "ldap.yaml");
        DateTime? mtime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;

        lock (_ldapConfigLock)
        {
            if (_ldapConfigCacheValid && _ldapConfigMtime == mtime)
                return _ldapConfigCache!;

            var cfg = _loader.LoadLdapConfig(_configDir);
            _ldapConfigMtime = mtime;
            _ldapConfigCache = cfg;
            _ldapConfigCacheValid = true;
            return cfg;
        }
    }

    /// <summary>Priority: local accounts first, then LDAP — same as apricot2. Returns null only when
    /// auth isn't configured at all (caller should allow access); throws <see cref="AuthException"/>
    /// with a user-facing message on any definitive failure — including a lockout rejection
    /// (<see cref="AuthException.StatusCode"/> 429), checked before spending any LDAP/BCrypt work.</summary>
    public AuthResult? Authenticate(string username, string password, string ip)
    {
        var cfg = GetLdapConfig();
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

    /// <summary>Shown to the caller for "unknown username" and "wrong password" alike, for both local
    /// and LDAP accounts — deliberately vague so a failed login can't be used to enumerate valid
    /// usernames. The specific reason still goes to <see cref="AuthException.LogDetail"/> for the
    /// server log.</summary>
    private const string InvalidCredentialsMessage = "Неверный логин или пароль.";

    private AuthResult? AuthenticateCore(string username, string password, LdapConfig cfg)
    {
        if (cfg.Local)
        {
            var localUser = LoadLocalUsers().FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
            // Always run BCrypt — against the real hash if the user exists, against a fixed dummy
            // hash otherwise — so "no such user" and "wrong password" take about the same time and
            // can't be told apart by a timing side-channel either.
            var passwordOk = PasswordHasher.Verify(password, localUser?.PasswordHash ?? PasswordHasher.DummyHash);
            if (localUser != null)
            {
                if (passwordOk)
                    return new AuthResult(username, localUser.IsAdmin, "local");
                // Username matched locally but the password didn't — don't fall through to LDAP for
                // the same username (same rule apricot2 applies).
                throw new AuthException(InvalidCredentialsMessage, logDetail: $"неверный пароль для локального пользователя «{username}»");
            }
        }

        if (cfg.Ldap)
        {
            var outcome = _ldap.Authenticate(username, password, cfg, id => ResolveSecret(id));
            if (outcome.Tag == LdapOutcomeTag.Success)
                return new AuthResult(outcome.Username, outcome.IsAdmin, "ldap", outcome.Domain);
            if (outcome.Tag is LdapOutcomeTag.NotFound or LdapOutcomeTag.WrongPassword)
                throw new AuthException(InvalidCredentialsMessage, logDetail: outcome.Message);
            // ConnError/CfgError/NoAccess are operational problems, not "wrong credentials" — the
            // specific message is still useful (and doesn't leak account existence) so show it as-is.
            throw new AuthException(outcome.Message);
        }

        throw new AuthException(InvalidCredentialsMessage, logDetail: $"локальный пользователь «{username}» не найден, LDAP не настроен");
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
