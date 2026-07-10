namespace Quince.Service.Configuration;

/// <summary>Maps to config/users.yaml — local accounts checked before LDAP (see AuthService).</summary>
public class UsersConfig
{
    public List<LocalUserEntry> Users { get; set; } = new();
}

public class LocalUserEntry
{
    public string Username { get; set; } = "";

    /// <summary>BCrypt hash, e.g. produced by `Quince.Service.exe --hash-password`.</summary>
    public string PasswordHash { get; set; } = "";

    public bool IsAdmin { get; set; }
}
