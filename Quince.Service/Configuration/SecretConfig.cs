namespace Quince.Service.Configuration;

/// <summary>Maps to config/secret.yaml — optional LDAP service-account credentials, referenced from
/// ldap.yaml by id (LdapConfig.BindSecret / LdapDomainConfig.BindSecret). Only needed for the
/// two-phase "service account searches for the user's DN, then re-bind as the user to check the
/// password" flow (see LdapAuthenticator) — without it, LDAP falls back to a single-phase bind using
/// the login's own credentials directly.</summary>
public class SecretConfig
{
    public List<SecretEntry> Authorization { get; set; } = new();
}

public class SecretEntry
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Domain { get; set; } = "";
}
