namespace Quince.Service.Configuration;

/// <summary>Maps to config/sessions.yaml — persisted session store so a login survives an app/service
/// restart, same as apricot2's sessions.yaml. Written on login/logout, pruned of expired entries and
/// reloaded at startup (see AuthService).</summary>
public class SessionsFile
{
    public Dictionary<string, SessionRecord> Sessions { get; set; } = new();
}

public class SessionRecord
{
    public string Username { get; set; } = "";
    public bool IsAdmin { get; set; }

    /// <summary>"local" or "ldap" — which path authenticated this session.</summary>
    public string AuthType { get; set; } = "";

    public string Domain { get; set; } = "";
    public string Ip { get; set; } = "";

    /// <summary>Unix seconds.</summary>
    public long CreatedAt { get; set; }

    /// <summary>Unix seconds — session is invalid once now() passes this.</summary>
    public long Expires { get; set; }
}
