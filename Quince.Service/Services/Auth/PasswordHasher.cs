namespace Quince.Service.Services.Auth;

/// <summary>Thin wrapper over BCrypt.Net-Next so callers don't reach for the library directly —
/// keeps the hashing scheme swappable in one place if it ever needs to change.</summary>
public static class PasswordHasher
{
    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password);

    public static bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // Malformed/foreign hash in users.yaml (e.g. hand-edited) — treat as "doesn't match"
            // rather than letting the exception bubble up as a 500 on a login attempt.
            return false;
        }
    }
}
