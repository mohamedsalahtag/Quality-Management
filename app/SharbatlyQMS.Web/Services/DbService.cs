using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public class DbService : IDbService
{
    private readonly string _connectionString;

    /// <summary>
    /// QMS settings share portal.SystemSetting with the SCM app, so every QMS
    /// key is stored prefixed to keep the two sets apart in one table.
    /// </summary>
    private const string ConfigPrefix = "qms.";

    public DbService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
    }

    private SqlConnection Open() => new(_connectionString);

    // ---- Configuration (portal.SystemSetting) -----------------------------
    // SystemSetting stores JSON, while QMS has always dealt in plain strings.
    // The two helpers below are the only place that conversion happens.

    private static string ToJson(string? value) =>
        JsonSerializer.Serialize(value);

    private static string? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<string>(json);
        }
        catch (JsonException)
        {
            // Tolerate a value written by hand (or by the SCM app) that is not
            // a JSON string literal - fall back to the raw text.
            return json;
        }
    }

    public async Task<string?> GetConfigAsync(string key)
    {
        using var c = Open();
        var json = await c.ExecuteScalarAsync<string?>(
            "SELECT SettingValueJson FROM portal.SystemSetting WHERE SettingKey = @key",
            new { key = ConfigPrefix + key });
        return FromJson(json);
    }

    public async Task SetConfigAsync(string key, string value, int? updatedBy)
    {
        using var c = Open();
        await c.ExecuteAsync(SetConfigSql,
            new { key = ConfigPrefix + key, value = ToJson(value), updatedBy });
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetConfigManyAsync(IEnumerable<string> keys)
    {
        var prefixed = keys.Select(k => ConfigPrefix + k).ToArray();
        if (prefixed.Length == 0) return new Dictionary<string, string?>();

        using var c = Open();
        var rows = await c.QueryAsync<(string SettingKey, string? SettingValueJson)>(
            "SELECT SettingKey, SettingValueJson FROM portal.SystemSetting WHERE SettingKey IN @prefixed",
            new { prefixed });
        // Strip the prefix again so callers keep using the bare QMS key names.
        return rows.ToDictionary(
            r => r.SettingKey[ConfigPrefix.Length..],
            r => FromJson(r.SettingValueJson));
    }

    public async Task SetConfigManyAsync(IEnumerable<KeyValuePair<string, string?>> entries, int? updatedBy)
    {
        var list = entries.ToList();
        if (list.Count == 0) return;
        using var c = Open();
        await c.OpenAsync();
        using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
        foreach (var kv in list)
            await c.ExecuteAsync(SetConfigSql,
                new { key = ConfigPrefix + kv.Key, value = ToJson(kv.Value ?? ""), updatedBy }, tx);
        tx.Commit();
    }

    private const string SetConfigSql = @"
        MERGE portal.SystemSetting AS t
        USING (SELECT @key AS SettingKey) AS s ON t.SettingKey = s.SettingKey
        WHEN MATCHED THEN UPDATE SET SettingValueJson = @value,
            UpdatedAt = SYSUTCDATETIME(), UpdatedByUserId = @updatedBy
        WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValueJson, UpdatedAt, UpdatedByUserId)
            VALUES (@key, @value, SYSUTCDATETIME(), @updatedBy);";

    // ---- Users -------------------------------------------------------------
    //
    // Identity lives in portal.User, which the SCM app also depends on. The
    // rule enforced here: QMS owns a user's ROLE, PROFILE and PLANT, and never
    // edits or deletes shared identity.
    //
    //   * reads      -> qms.AppUser (portal.User + qms.UserProfile + role + plant)
    //   * disable    -> qms.UserProfile.DisabledAt, so QMS access is revoked
    //                   without locking the person out of SCM
    //   * "delete"   -> revoke Qc roles + drop the profile row, which removes
    //                   them from QMS entirely; the portal.User row survives
    //   * create     -> reuses an existing portal.User when the username already
    //                   exists, and only inserts a new one when it does not

    private const string UserSelect = @"
        SELECT UserId, EmployeeId, Username, FullName, Email, Department,
               ProfilePicture, PasswordHash, Role, PlantCode, IsActive, IsOnline,
               LastLogin, LastSeen, CreatedAt, CreatedBy, DisabledAt, DisabledBy
        FROM qms.AppUser";

    /// <summary>Maps a QMS role name onto its namespaced portal.Role code.</summary>
    private static string ToRoleCode(string? role) => role switch
    {
        UserRoles.SiteAdmin    => "QcAdmin",
        UserRoles.Manager      => "QcManager",
        UserRoles.ClaimManager => "QcClaimManager",
        UserRoles.Supervisor   => "QcSupervisor",
        UserRoles.Operator     => "QcOperator",
        _                      => "QcViewer",
    };

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
        await c.OpenAsync();
        using var tx = (SqlTransaction)await c.BeginTransactionAsync();

        // Reuse the portal identity when the username is already known - the
        // person may well be an existing SCM user being granted quality access.
        var userId = await c.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 UserId FROM portal.[User]
            WHERE Username = @Username OR SamAccountName = @Username
            ORDER BY CASE WHEN Username = @Username THEN 0 ELSE 1 END",
            new { u.Username }, tx);

        userId ??= await c.ExecuteScalarAsync<int>(@"
            INSERT INTO portal.[User]
                (IdType, Username, DisplayName, Email, SamAccountName,
                 MfaEnabled, IsActive, CreatedAt, UpdatedAt)
            OUTPUT INSERTED.UserId
            VALUES ('LDAP', @Username, @FullName, @Email, @Username,
                    0, @IsActive, SYSUTCDATETIME(), SYSUTCDATETIME());",
            new { u.Username, u.FullName, u.Email, u.IsActive }, tx);

        await UpsertQmsUserAsync(c, tx, userId.Value, u);
        tx.Commit();
        return userId.Value;
    }

    public async Task UpdateUserAsync(User u)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = (SqlTransaction)await c.BeginTransactionAsync();

        // Display name and e-mail are only rewritten for accounts that exist
        // solely for QMS. For anyone who also holds an SCM role, those fields
        // are mastered in the portal / AD and must not be edited from here.
        await c.ExecuteAsync(@"
            UPDATE u SET DisplayName = @FullName, Email = @Email, UpdatedAt = SYSUTCDATETIME()
            FROM portal.[User] AS u
            WHERE u.UserId = @UserId
              AND NOT EXISTS (SELECT 1 FROM portal.UserRole ur
                              WHERE ur.UserId = u.UserId AND ur.RoleCode NOT LIKE 'Qc%');",
            new { u.UserId, u.FullName, u.Email }, tx);

        await UpsertQmsUserAsync(c, tx, u.UserId, u);
        tx.Commit();
    }

    /// <summary>
    /// Writes the parts of a user QMS owns: profile attributes, the single Qc
    /// role, and the plant restriction. Shared identity is left untouched.
    /// </summary>
    private static async Task UpsertQmsUserAsync(SqlConnection c, SqlTransaction tx, int userId, User u)
    {
        await c.ExecuteAsync(@"
            MERGE qms.UserProfile AS t
            USING (SELECT @userId AS UserId) AS s ON t.UserId = s.UserId
            WHEN MATCHED THEN UPDATE SET EmployeeId = @EmployeeId, Department = @Department
            WHEN NOT MATCHED THEN INSERT (UserId, EmployeeId, Department, IsOnline, CreatedBy)
                VALUES (@userId, @EmployeeId, @Department, 0, @CreatedBy);",
            new { userId, u.EmployeeId, u.Department, u.CreatedBy }, tx);

        // Exactly one Qc role per user, mirroring the single Role column the
        // application model carries.
        await c.ExecuteAsync(@"
            DELETE FROM portal.UserRole WHERE UserId = @userId AND RoleCode LIKE 'Qc%';
            INSERT INTO portal.UserRole (UserId, RoleCode, SourceKind)
            VALUES (@userId, @roleCode, 'MANUAL');",
            new { userId, roleCode = ToRoleCode(u.Role) }, tx);

        await c.ExecuteAsync(@"
            DELETE FROM portal.UserPlant WHERE UserId = @userId;
            INSERT INTO portal.UserPlant (UserId, Plant, AssignedAt, AssignedByUserId)
            SELECT @userId, @plant, SYSUTCDATETIME(), NULL
            WHERE @plant IS NOT NULL AND LEN(@plant) > 0;",
            new { userId, plant = u.PlantCode }, tx);
    }

    public async Task UpdateProfilePictureAsync(int userId, string path)
    {
        // Shared on purpose: one identity, one avatar across both apps.
        using var c = Open();
        await c.ExecuteAsync(
            "UPDATE portal.[User] SET ProfilePicturePath = @path, UpdatedAt = SYSUTCDATETIME() WHERE UserId = @userId",
            new { userId, path });
    }

    public async Task UpdateLastSeenAsync(int userId)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE portal.[User] SET LastLoginAt = SYSUTCDATETIME() WHERE UserId = @userId;
            UPDATE qms.UserProfile SET LastSeen = SYSUTCDATETIME() WHERE UserId = @userId;",
            new { userId });
    }

    public async Task SetUserOnlineAsync(int userId, bool online)
    {
        using var c = Open();
        await c.ExecuteAsync(
            "UPDATE qms.UserProfile SET IsOnline = @online, LastSeen = SYSUTCDATETIME() WHERE UserId = @userId",
            new { userId, online });
    }

    public async Task SetUserActiveAsync(int userId, bool active, int? changedBy)
    {
        // Recorded against the QMS profile only. Flipping portal.User.IsActive
        // here would sign the person out of the SCM app as a side effect.
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE qms.UserProfile SET
              DisabledAt = CASE WHEN @active = 0 THEN SYSUTCDATETIME() ELSE NULL END,
              DisabledBy = CASE WHEN @active = 0 THEN @changedBy ELSE NULL END
            WHERE UserId = @userId",
            new { userId, active, changedBy });
    }

    public async Task<bool> TryDeleteUserAsync(int userId)
    {
        // Removes the user from QMS by revoking their quality roles and profile.
        // The portal.User row is deliberately left alone - it may be someone's
        // SCM login, and QMS is not the owner of shared identity.
        using var c = Open();
        try
        {
            await c.ExecuteAsync(@"
                DELETE FROM portal.UserRole WHERE UserId = @userId AND RoleCode LIKE 'Qc%';
                DELETE FROM portal.UserPlant WHERE UserId = @userId;
                DELETE FROM qms.UserProfile WHERE UserId = @userId;",
                new { userId });
            return true;
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            return false; // FK reference - caller should suggest disabling instead
        }
    }

    public async Task<IReadOnlyList<string>> GetUserRolesAsync(int userId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<string>(@"
            SELECT CASE RoleCode
                       WHEN 'QcAdmin'        THEN 'SiteAdmin'
                       WHEN 'QcManager'      THEN 'Manager'
                       WHEN 'QcClaimManager' THEN 'ClaimManager'
                       WHEN 'QcSupervisor'   THEN 'Supervisor'
                       WHEN 'QcOperator'     THEN 'Operator'
                       ELSE 'Viewer'
                   END
            FROM portal.UserRole
            WHERE UserId = @userId AND RoleCode LIKE 'Qc%'",
            new { userId });
        return rows.ToList();
    }

    public async Task<bool> TryGrantDefaultAccessAsync(string username)
    {
        using var c = Open();
        var userId = await c.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 UserId FROM portal.[User]
            WHERE Username = @username OR SamAccountName = @username
            ORDER BY CASE WHEN Username = @username THEN 0 ELSE 1 END",
            new { username });
        if (userId is null) return false;

        await c.ExecuteAsync(@"
            IF NOT EXISTS (SELECT 1 FROM portal.UserRole WHERE UserId = @userId AND RoleCode LIKE 'Qc%')
                INSERT INTO portal.UserRole (UserId, RoleCode, SourceKind)
                VALUES (@userId, 'QcViewer', 'LDAP');

            IF NOT EXISTS (SELECT 1 FROM qms.UserProfile WHERE UserId = @userId)
                INSERT INTO qms.UserProfile (UserId, IsOnline) VALUES (@userId, 0);",
            new { userId });
        return true;
    }
}
