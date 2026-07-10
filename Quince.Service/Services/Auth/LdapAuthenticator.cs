using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Sockets;
using Quince.Service.Configuration;

namespace Quince.Service.Services.Auth;

public enum LdapOutcomeTag { Success, NotFound, ConnError, WrongPassword, CfgError, NoAccess }

public record LdapOutcome(LdapOutcomeTag Tag, string Username = "", bool IsAdmin = false, string Domain = "", string Message = "");

/// <summary>Ports apricot2's app/auth.py LDAP logic (transitive nested-group lookup, multi-domain
/// dispatch, DOMAIN\user / user@domain / bare username parsing) onto
/// System.DirectoryServices.Protocols. Binds via simple LDAP bind (AuthType.Basic) using a UPN
/// ("user@domain.suffix") rather than apricot2's NTLM option — this app only needs simple bind.</summary>
public class LdapAuthenticator
{
    private const string TransitiveGroupsFilterOid = "1.2.840.113556.1.4.1941";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Tries every domain that matches the username's domain hint (or all configured domains
    /// if it has none), stopping at the first definitive result. NotFound/ConnError move on to the
    /// next candidate domain; WrongPassword/CfgError stop immediately (mirrors apricot2's
    /// _authenticate_ldap dispatcher).</summary>
    public LdapOutcome Authenticate(string username, string password, LdapConfig cfg, Func<int, SecretEntry?> resolveSecret)
    {
        var (shortName, hint) = ParseUsername(username);
        var domains = GetDomainConfigs(cfg);
        if (domains.Count == 0)
            return new LdapOutcome(LdapOutcomeTag.CfgError, Message: "LDAP включён, но в ldap.yaml не настроен ни один домен.");

        var candidates = hint != null ? domains.Where(d => DomainMatches(d, hint)).ToList() : domains;
        if (hint != null && candidates.Count == 0)
            return new LdapOutcome(LdapOutcomeTag.NotFound, Message: $"Домен «{hint}» не найден.");

        LdapOutcome? last = null;
        foreach (var dcfg in candidates)
        {
            var secret = dcfg.BindSecret.HasValue ? resolveSecret(dcfg.BindSecret.Value) : null;
            var outcome = AuthenticateAgainstDomain(shortName, password, dcfg, secret);
            if (outcome.Tag == LdapOutcomeTag.Success) return outcome;
            if (outcome.Tag is LdapOutcomeTag.WrongPassword or LdapOutcomeTag.CfgError or LdapOutcomeTag.NoAccess) return outcome;
            last = outcome;
        }
        return last ?? new LdapOutcome(LdapOutcomeTag.NotFound, Message: $"Пользователь «{shortName}» не найден.");
    }

    private LdapOutcome AuthenticateAgainstDomain(string shortName, string password, LdapDomainConfig dcfg, SecretEntry? secret)
    {
        if (string.IsNullOrWhiteSpace(dcfg.Server) || string.IsNullOrWhiteSpace(dcfg.BaseDn))
            return new LdapOutcome(LdapOutcomeTag.CfgError, Message: $"[{DomainLabel(dcfg)}] В ldap.yaml не задан server/base_dn.");

        var (host, port, useSsl) = ParseServerUrl(dcfg.Server);
        var upnSuffix = !string.IsNullOrEmpty(dcfg.UpnSuffix) ? dcfg.UpnSuffix : BaseDnToUpnSuffix(dcfg.BaseDn);
        var adminGroups = dcfg.AdminGroups ?? new List<string>();
        var accessGroups = dcfg.AccessGroups ?? new List<string>();

        List<string> memberOf;

        if (secret != null)
        {
            using var svcConn = Connect(host, port, useSsl);
            var svcUpn = !string.IsNullOrEmpty(upnSuffix) ? $"{secret.Username}@{upnSuffix}" : secret.Username;
            try
            {
                svcConn.Credential = new NetworkCredential(svcUpn, secret.Password);
                svcConn.Bind();
            }
            catch (Exception ex) when (IsConnectivityError(ex))
            {
                return new LdapOutcome(LdapOutcomeTag.ConnError,
                    Message: $"[{DomainLabel(dcfg)}] Нет связи с сервером AD «{host}:{port}»: {ex.Message}");
            }
            catch (Exception ex)
            {
                return new LdapOutcome(LdapOutcomeTag.CfgError,
                    Message: $"[{DomainLabel(dcfg)}] Сервисный аккаунт «{svcUpn}» не прошёл аутентификацию — проверьте логин и пароль bind-пользователя (secret) в ldap.yaml: {ex.Message}");
            }

            SearchedUser? found;
            try { found = SearchUser(svcConn, dcfg.BaseDn, shortName); }
            catch (Exception ex)
            {
                return new LdapOutcome(LdapOutcomeTag.ConnError, Message: $"[{DomainLabel(dcfg)}] Ошибка поиска пользователя: {ex.Message}");
            }
            if (found == null)
                return new LdapOutcome(LdapOutcomeTag.NotFound, Message: $"[{DomainLabel(dcfg)}] Пользователь «{shortName}» не найден.");

            memberOf = ResolveGroups(svcConn, dcfg.BaseDn, found.Dn, found.PrimaryGroupId, found.DirectMemberOf);

            using var userConn = Connect(host, port, useSsl);
            var userUpn = !string.IsNullOrEmpty(upnSuffix) ? $"{shortName}@{upnSuffix}" : shortName;
            try
            {
                userConn.Credential = new NetworkCredential(userUpn, password);
                userConn.Bind();
            }
            catch (LdapException ex) when (IsInvalidCredentials(ex))
            {
                return new LdapOutcome(LdapOutcomeTag.WrongPassword, Message: $"Неверный пароль для пользователя «{shortName}».");
            }
            catch (Exception ex)
            {
                return new LdapOutcome(LdapOutcomeTag.ConnError, Message: $"[{DomainLabel(dcfg)}] Ошибка проверки пароля: {ex.Message}");
            }
        }
        else
        {
            using var conn = Connect(host, port, useSsl);
            var bindUpn = !string.IsNullOrEmpty(upnSuffix) ? $"{shortName}@{upnSuffix}" : shortName;
            try
            {
                conn.Credential = new NetworkCredential(bindUpn, password);
                conn.Bind();
            }
            catch (LdapException ex) when (IsInvalidCredentials(ex))
            {
                return new LdapOutcome(LdapOutcomeTag.WrongPassword,
                    Message: $"[{DomainLabel(dcfg)}] Не удалось войти как «{bindUpn}». Проверьте имя пользователя и пароль.");
            }
            catch (Exception ex)
            {
                return new LdapOutcome(LdapOutcomeTag.ConnError, Message: $"[{DomainLabel(dcfg)}] Ошибка подключения: {ex.Message}");
            }

            SearchedUser? found;
            try { found = SearchUser(conn, dcfg.BaseDn, shortName); }
            catch { found = null; } // best-effort — password already verified, just can't resolve groups
            memberOf = found == null ? new List<string>() : ResolveGroups(conn, dcfg.BaseDn, found.Dn, found.PrimaryGroupId, found.DirectMemberOf);
        }

        if (accessGroups.Count > 0 && !memberOf.Any(g => accessGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))
            return new LdapOutcome(LdapOutcomeTag.NoAccess,
                Message: $"Пользователь «{shortName}» аутентифицирован, но не входит ни в одну из разрешённых групп доступа.");

        var isAdmin = memberOf.Any(g => adminGroups.Contains(g, StringComparer.OrdinalIgnoreCase));
        return new LdapOutcome(LdapOutcomeTag.Success, shortName, isAdmin, DomainLabel(dcfg));
    }

    private static bool IsInvalidCredentials(LdapException ex) => ex.ErrorCode == 49;

    /// <summary>True for network/transport-level failures (server unreachable, DNS failure, timeout)
    /// as opposed to protocol-level bind failures (wrong password, bad bind DN) — lets the service-
    /// account bind above report "нет связи с AD" separately from "сервисный аккаунт не настроен",
    /// instead of lumping both under one generic "не прошёл аутентификацию". Error codes are the
    /// native wldap32 constants surfaced through LdapException.ErrorCode: 81 SERVER_DOWN, 82
    /// LOCAL_ERROR, 85 TIMEOUT, 91 CONNECT_ERROR.</summary>
    private static bool IsConnectivityError(Exception ex) =>
        ex is SocketException ||
        ex.InnerException is SocketException ||
        ex is TimeoutException ||
        (ex is LdapException lex && lex.ErrorCode is 81 or 82 or 85 or 91);

    private static string DomainLabel(LdapDomainConfig d) => !string.IsNullOrEmpty(d.Name) ? d.Name : (d.Server ?? "?");

    private record SearchedUser(string Dn, string? PrimaryGroupId, List<string> DirectMemberOf);

    private static SearchedUser? SearchUser(LdapConnection conn, string baseDn, string shortName)
    {
        var req = new SearchRequest(baseDn, $"(sAMAccountName={EscapeFilterValue(shortName)})", SearchScope.Subtree,
            "distinguishedName", "primaryGroupID", "memberOf");
        var resp = (SearchResponse)conn.SendRequest(req);
        if (resp.Entries.Count == 0) return null;

        var entry = resp.Entries[0];
        var dn = entry.DistinguishedName;
        string? primaryGroupId = entry.Attributes.Contains("primaryGroupID")
            ? entry.Attributes["primaryGroupID"][0]?.ToString()
            : null;
        var memberOf = new List<string>();
        if (entry.Attributes.Contains("memberOf"))
            foreach (var v in entry.Attributes["memberOf"].GetValues(typeof(string)))
                memberOf.Add((string)v);
        return new SearchedUser(dn, primaryGroupId, memberOf);
    }

    /// <summary>Transitive nested-group membership via the AD-specific LDAP_MATCHING_RULE_IN_CHAIN
    /// OID (resolves nested group chains server-side), plus the user's primary group (AD stores that
    /// as a RID, not a direct memberOf entry — same two-step apricot2 uses). Falls back to the
    /// already-fetched direct memberOf list if the transitive query itself fails.</summary>
    private static List<string> ResolveGroups(LdapConnection conn, string baseDn, string userDn, string? primaryGroupId, List<string> directMemberOf)
    {
        var groups = TryTransitiveGroups(conn, baseDn, userDn) ?? new List<string>(directMemberOf);
        var primaryDn = TryPrimaryGroupDn(conn, baseDn, primaryGroupId);
        if (primaryDn != null) groups.Add(primaryDn);
        return groups;
    }

    private static List<string>? TryTransitiveGroups(LdapConnection conn, string baseDn, string userDn)
    {
        try
        {
            var filter = $"(member:{TransitiveGroupsFilterOid}:={EscapeFilterValue(userDn)})";
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, "distinguishedName");
            var resp = (SearchResponse)conn.SendRequest(req);
            var result = new List<string>();
            foreach (SearchResultEntry e in resp.Entries)
                result.Add(e.DistinguishedName);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryPrimaryGroupDn(LdapConnection conn, string baseDn, string? primaryGroupId)
    {
        if (string.IsNullOrEmpty(primaryGroupId)) return null;
        try
        {
            var req = new SearchRequest(baseDn, $"(primaryGroupToken={primaryGroupId})", SearchScope.Subtree, "distinguishedName");
            var resp = (SearchResponse)conn.SendRequest(req);
            return resp.Entries.Count > 0 ? resp.Entries[0].DistinguishedName : null;
        }
        catch
        {
            return null;
        }
    }

    private static LdapConnection Connect(string host, int port, bool useSsl)
    {
        var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Timeout = ConnectTimeout,
        };
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (useSsl) conn.SessionOptions.SecureSocketLayer = true;
        return conn;
    }

    /// <summary>"ldap://dc01.corp.local", "ldaps://dc01.corp.local:636", "dc01.corp.local:3268" (Global
    /// Catalog) — all accepted, same URL shapes apricot2's ldap.yaml examples use.</summary>
    internal static (string Host, int Port, bool UseSsl) ParseServerUrl(string server)
    {
        var s = server.Trim();
        var useSsl = s.StartsWith("ldaps://", StringComparison.OrdinalIgnoreCase);
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];

        var parts = s.Split(':', 2);
        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : (useSsl ? 636 : 389);
        return (host, port, useSsl);
    }

    /// <summary>"DC=corp,DC=local" -> "corp.local".</summary>
    internal static string BaseDnToUpnSuffix(string baseDn) =>
        string.Join(".", baseDn.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..]));

    /// <summary>DOMAIN\user -> (user, "DOMAIN"); user@corp.local -> (user, "corp.local"); user -> (user, null).</summary>
    internal static (string ShortName, string? DomainHint) ParseUsername(string raw)
    {
        var backslash = raw.IndexOf('\\');
        if (backslash >= 0)
            return (raw[(backslash + 1)..].Trim(), raw[..backslash].Trim().ToUpperInvariant());

        var at = raw.IndexOf('@');
        if (at >= 0)
            return (raw[..at].Trim(), raw[(at + 1)..].Trim().ToLowerInvariant());

        return (raw.Trim(), null);
    }

    private static bool DomainMatches(LdapDomainConfig cfg, string hint)
    {
        var hintUpper = hint.ToUpperInvariant();
        var hintLower = hint.ToLowerInvariant();

        if (!string.IsNullOrEmpty(cfg.Name) && cfg.Name.ToUpperInvariant() == hintUpper)
            return true;

        var upn = !string.IsNullOrEmpty(cfg.UpnSuffix) ? cfg.UpnSuffix : BaseDnToUpnSuffix(cfg.BaseDn);
        return !string.IsNullOrEmpty(upn) && upn.ToLowerInvariant() == hintLower;
    }

    /// <summary>Normalizes the single-domain (top-level server/domain/base_dn) and multi-domain
    /// (domains: list) config forms into one list — each domain entry inherits the top-level
    /// AdminGroups when it doesn't set its own (same as apricot2's _get_domain_configs).</summary>
    internal static List<LdapDomainConfig> GetDomainConfigs(LdapConfig cfg)
    {
        if (cfg.Domains.Count > 0)
        {
            foreach (var d in cfg.Domains)
            {
                d.AdminGroups ??= cfg.AdminGroups;
                d.AccessGroups ??= cfg.AccessGroups;
            }
            return cfg.Domains;
        }

        if (string.IsNullOrWhiteSpace(cfg.Server)) return new List<LdapDomainConfig>();

        return new List<LdapDomainConfig>
        {
            new()
            {
                Name = cfg.Domain,
                Server = cfg.Server,
                BaseDn = cfg.BaseDn,
                BindSecret = cfg.BindSecret,
                AdminGroups = cfg.AdminGroups,
                AccessGroups = cfg.AccessGroups,
            },
        };
    }

    /// <summary>RFC 4515 filter-value escaping — needed both for user-supplied usernames and for DNs
    /// embedded back into a filter (the transitive-groups query).</summary>
    private static string EscapeFilterValue(string value) =>
        value
            .Replace("\\", "\\5c")
            .Replace("*", "\\2a")
            .Replace("(", "\\28")
            .Replace(")", "\\29")
            .Replace("\0", "\\00");
}
