using YamlDotNet.Serialization;

namespace Quince.Service.Configuration;

/// <summary>Maps to config/ldap.yaml. The file's mere presence on disk is the authorization on/off
/// switch (see YamlConfigLoader.LoadLdapConfig) — absent means the app stays open, matching today's
/// behavior; present means the login page gates the app via Local and/or LDAP below.</summary>
public class LdapConfig
{
    /// <summary>True once ldap.yaml was actually found on disk — set by the loader, never present in
    /// the YAML itself.</summary>
    [YamlIgnore]
    public bool Present { get; set; }

    // apricot2 itself writes these two capitalized ("Local:"/"LDAP:") while everything else in the
    // file is lowercase-underscored. YamlConfigLoader.LoadLdapConfig lowercases just these two key
    // names (case-insensitively) before deserializing, so both stay bindable as plain lowercase
    // properties here regardless of how the file capitalizes them.
    public bool Local { get; set; }
    public bool Ldap { get; set; }

    /// <summary>AD group DNs whose members get IsAdmin — applies to every domain below that doesn't
    /// override it locally (see LdapDomainConfig.AdminGroups).</summary>
    public List<string> AdminGroups { get; set; } = new();

    /// <summary>When non-empty, only members of these groups may log in at all (an allow-list on top
    /// of successful authentication) — applies to every domain below that doesn't override it
    /// locally. Empty/unset means any user who authenticates against a configured domain gets in,
    /// same as apricot2's default.</summary>
    public List<string> AccessGroups { get; set; } = new();

    // Single-domain form (back-compat / the common case) — ignored if Domains is non-empty.
    public string Server { get; set; } = "";
    public string Domain { get; set; } = "";
    public string BaseDn { get; set; } = "";
    public int? BindSecret { get; set; }

    // Multi-domain / trusted-domains form.
    public List<LdapDomainConfig> Domains { get; set; } = new();

    /// <summary>Failed-login lockout — off by default so an existing ldap.yaml without this section
    /// behaves exactly as before.</summary>
    public LockoutConfig Lockout { get; set; } = new();
}

/// <summary>Maps to config/ldap.yaml's `lockout:` section — failed-login throttling tracked in
/// memory by <see cref="Services.Auth.LoginLockoutTracker"/>, applied to both local (users.yaml) and
/// LDAP logins alike since both go through the same <see cref="Services.Auth.AuthService.Authenticate"/>
/// funnel. Deliberately not persisted to disk (unlike sessions.yaml): the goal is slowing down a
/// live brute-force attempt, not remembering one across a service restart.</summary>
public class LockoutConfig
{
    public bool Enabled { get; set; }

    /// <summary>Consecutive failed attempts for one username before that username gets locked out.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long a locked-out username stays locked, in seconds.</summary>
    public int LockoutSeconds { get; set; } = 300;

    /// <summary>How many separate lockout events sourced from the same IP address (whether the same
    /// username repeatedly, or several different ones) before that IP itself gets blocked outright,
    /// regardless of username.</summary>
    public int MaxLockoutCycles { get; set; } = 3;

    /// <summary>How long an escalated IP block lasts, in seconds.</summary>
    public int IpLockoutSeconds { get; set; } = 3600;
}

public class LdapDomainConfig
{
    /// <summary>NetBIOS-style short name, matched against a "DOMAIN\user" login hint.</summary>
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string BaseDn { get; set; } = "";
    public int? BindSecret { get; set; }

    /// <summary>Overrides the top-level AdminGroups for this domain specifically when set.</summary>
    public List<string>? AdminGroups { get; set; }

    /// <summary>Overrides the top-level AccessGroups for this domain specifically when set.</summary>
    public List<string>? AccessGroups { get; set; }

    /// <summary>Explicit "user@suffix" match override — derived from BaseDn when not set.</summary>
    public string UpnSuffix { get; set; } = "";
}
