using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public interface IDbService
{
    // ---- SiteConfiguration (key/value) -----------------------------------
    Task<string?> GetConfigAsync(string key);
    Task SetConfigAsync(string key, string value, int? updatedBy);
    /// <summary>Writes many config keys in a single transaction so partial
    /// failure cannot leave the row group in a mixed state.</summary>
    Task SetConfigManyAsync(IEnumerable<KeyValuePair<string, string?>> entries, int? updatedBy);
    Task<IReadOnlyDictionary<string, string?>> GetConfigManyAsync(IEnumerable<string> keys);

    // ---- Users -----------------------------------------------------------
    Task<User?> GetUserByIdAsync(int userId);
    Task<User?> GetUserByUsernameAsync(string username);
    Task<IReadOnlyList<User>> ListUsersAsync(string? search, string? role, bool? isActive);
    Task<int>  CreateUserAsync(User user);
    Task UpdateUserAsync(User user);
    Task UpdateProfilePictureAsync(int userId, string path);
    Task UpdateLastSeenAsync(int userId);
    Task SetUserOnlineAsync(int userId, bool online);
    Task SetUserActiveAsync(int userId, bool active, int? changedBy);
    Task<bool> TryDeleteUserAsync(int userId);

    /// <summary>
    /// Every QMS role the user holds, as role names from <see cref="UserRoles"/>.
    /// The User model carries a single role, but portal.UserRole is a
    /// many-to-many, so an admin can grant a peer role (e.g. ClaimManager
    /// alongside Manager) and both are emitted as claims at sign-in.
    /// </summary>
    Task<IReadOnlyList<string>> GetUserRolesAsync(int userId);

    /// <summary>
    /// Grants the default QMS role to someone who already exists in the shared
    /// portal identity store but has no quality access yet. Returns false when
    /// no portal account exists, in which case login must be refused: QMS does
    /// not invent rows in an identity store the SCM app also depends on.
    /// </summary>
    Task<bool> TryGrantDefaultAccessAsync(string username);
}
