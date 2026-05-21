using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public class DbService : IDbService
{
    private readonly string _connectionString;

    public DbService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
    }

    private SqlConnection Open() => new(_connectionString);

    // ---- SiteConfiguration ------------------------------------------------
    public async Task<string?> GetConfigAsync(string key)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<string?>(
            "SELECT ConfigValue FROM SiteConfiguration WHERE ConfigKey = @key", new { key });
    }

    public async Task SetConfigAsync(string key, string value, int? updatedBy)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            MERGE SiteConfiguration AS t
            USING (SELECT @key AS ConfigKey) AS s ON t.ConfigKey = s.ConfigKey
            WHEN MATCHED THEN UPDATE SET ConfigValue = @value,
                UpdatedAt = SYSUTCDATETIME(), UpdatedBy = @updatedBy
            WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigValue, UpdatedAt, UpdatedBy)
                VALUES (@key, @value, SYSUTCDATETIME(), @updatedBy);",
            new { key, value, updatedBy });
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetConfigManyAsync(IEnumerable<string> keys)
    {
        using var c = Open();
        var rows = await c.QueryAsync<(string ConfigKey, string? ConfigValue)>(
            "SELECT ConfigKey, ConfigValue FROM SiteConfiguration WHERE ConfigKey IN @keys",
            new { keys });
        return rows.ToDictionary(r => r.ConfigKey, r => r.ConfigValue);
    }

    // ---- Users -----------------------------------------------------------
    private const string UserSelect = @"
        SELECT UserId, EmployeeId, Username, FullName, Email, Department,
               ProfilePicture, PasswordHash, Role, IsActive, IsOnline,
               LastLogin, LastSeen, CreatedAt, CreatedBy, DisabledAt, DisabledBy
        FROM Users";

    public async Task<User?> GetUserByIdAsync(int userId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<User>(
            UserSelect + " WHERE UserId = @userId", new { userId });
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<User>(
            UserSelect + " WHERE Username = @username", new { username });
    }

    public async Task<IReadOnlyList<User>> ListUsersAsync(string? search, string? role, bool? isActive)
    {
        using var c = Open();
        var sql = UserSelect + @"
            WHERE (@search IS NULL OR
                   FullName LIKE '%' + @search + '%' OR
                   Username LIKE '%' + @search + '%' OR
                   Email    LIKE '%' + @search + '%')
              AND (@role IS NULL OR Role = @role)
              AND (@isActive IS NULL OR IsActive = @isActive)
            ORDER BY FullName";
        var rows = await c.QueryAsync<User>(sql, new { search, role, isActive });
        return rows.ToList();
    }

    public async Task<int> CreateUserAsync(User u)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<int>(@"
            INSERT INTO Users
                (EmployeeId, Username, FullName, Email, Department, ProfilePicture,
                 PasswordHash, Role, IsActive, CreatedAt, CreatedBy)
            VALUES
                (@EmployeeId, @Username, @FullName, @Email, @Department, @ProfilePicture,
                 @PasswordHash, @Role, @IsActive, SYSUTCDATETIME(), @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS INT);", u);
    }

    public async Task UpdateUserAsync(User u)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE Users SET
              EmployeeId = @EmployeeId, FullName = @FullName, Email = @Email,
              Department = @Department, PasswordHash = @PasswordHash,
              Role = @Role, IsActive = @IsActive
            WHERE UserId = @UserId", u);
    }

    public async Task UpdateProfilePictureAsync(int userId, string path)
    {
        using var c = Open();
        await c.ExecuteAsync(
            "UPDATE Users SET ProfilePicture = @path WHERE UserId = @userId",
            new { userId, path });
    }

    public async Task UpdateLastSeenAsync(int userId)
    {
        using var c = Open();
        await c.ExecuteAsync(
            "UPDATE Users SET LastLogin = SYSUTCDATETIME(), LastSeen = SYSUTCDATETIME() WHERE UserId = @userId",
            new { userId });
    }

    public async Task SetUserOnlineAsync(int userId, bool online)
    {
        using var c = Open();
        await c.ExecuteAsync(
            "UPDATE Users SET IsOnline = @online, LastSeen = SYSUTCDATETIME() WHERE UserId = @userId",
            new { userId, online });
    }

    public async Task SetUserActiveAsync(int userId, bool active, int? changedBy)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE Users SET IsActive = @active,
              DisabledAt = CASE WHEN @active = 0 THEN SYSUTCDATETIME() ELSE NULL END,
              DisabledBy = CASE WHEN @active = 0 THEN @changedBy ELSE NULL END
            WHERE UserId = @userId",
            new { userId, active, changedBy });
    }

    public async Task<bool> TryDeleteUserAsync(int userId)
    {
        using var c = Open();
        try
        {
            await c.ExecuteAsync("DELETE FROM Users WHERE UserId = @userId", new { userId });
            return true;
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            return false; // FK reference - caller should suggest disabling instead
        }
    }
}
