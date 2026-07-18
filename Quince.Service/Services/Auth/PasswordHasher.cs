namespace Quince.Service.Services.Auth;

/// <summary>Thin wrapper over BCrypt.Net-Next so callers don't reach for the library directly —
/// keeps the hashing scheme swappable in one place if it ever needs to change.</summary>
public static class PasswordHasher
{
    /// <summary>A BCrypt hash with no known matching plaintext, generated once at process start.
    /// Verify against this for a username that doesn't exist so the login endpoint takes roughly the
    /// same time either way — otherwise "no such user" returns near-instantly while "wrong password"
    /// pays for a real BCrypt comparison, letting an attacker enumerate valid usernames by timing.</summary>
    public static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"));

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
