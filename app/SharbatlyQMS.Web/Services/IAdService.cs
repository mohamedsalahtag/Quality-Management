namespace SharbatlyQMS.Web.Services;

/// <summary>
/// LDAP bind-only authentication. The host app calls these on every login
/// attempt when AD is configured; falls back to local BCrypt when AD is
/// off or the bind fails.
/// </summary>
public interface IAdService
{
    /// <summary>
    /// Tries an LDAP bind with the supplied credentials against the
    /// configured domain. Returns ok=true plus a populated <see cref="AdUserInfo"/>
    /// when the bind succeeds. ok=false with a message when AD is off,
    /// the network is unreachable, or the credentials are wrong.
    /// </summary>
    Task<(bool ok, AdUserInfo? info, string? error)> AuthenticateAsync(
        string username, string password, AdConfig cfg);

    /// <summary>
    /// Verifies that the supplied AD config can reach the directory.
    /// Called from the admin "Test connection" button without saving the
    /// config first.
    /// </summary>
    Task<(bool ok, string message)> TestConnectionAsync(AdConfig cfg);

    /// <summary>
    /// Lists active AD users matching the optional substring filter
    /// (sAMAccountName or displayName). Capped at <paramref name="max"/>.
    /// Results are cached in memory for a short window so the admin picker
    /// doesn't hammer the domain controller.
    /// </summary>
    Task<List<AdUserInfo>> ListUsersAsync(AdConfig cfg, string? filter, int max = 200);

    /// <summary>
    /// Returns the AD profile for a single sAMAccountName, or null if no
    /// such account exists. Used to verify a typed/posted username before
    /// creating a local Users row.
    /// </summary>
    Task<AdUserInfo?> GetUserInfoAsync(string username, AdConfig cfg);
}

/// <summary>
/// Minimal slice of an AD user account returned to the host. Mirrors the
/// fields the host already stores on its local <c>Users</c> row so an
/// auto-create can populate sensible defaults.
/// </summary>
public class AdUserInfo
{
    public string Username   { get; set; } = "";
    public string FullName   { get; set; } = "";
    public string? Email     { get; set; }
    public string? Department{ get; set; }
}
