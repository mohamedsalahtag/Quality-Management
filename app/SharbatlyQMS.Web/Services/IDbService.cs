using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public interface IDbService
{
    // ---- SiteConfiguration (key/value) -----------------------------------
    Task<string?> GetConfigAsync(string key);
    Task SetConfigAsync(string key, string value, int? updatedBy);
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
}
