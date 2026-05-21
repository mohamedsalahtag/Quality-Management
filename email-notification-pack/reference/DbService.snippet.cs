// Methods that EmailService.cs calls on your DB layer. Add these to your
// existing DbService (or its interface) and adapt the SQL to your provider.
// Reference uses Dapper + Microsoft.Data.SqlClient.

using Dapper;
using Microsoft.Data.SqlClient;
using HelpDesk.Models;

namespace HelpDesk.Services;

public partial interface IDbService
{
    Task<Dictionary<string, string>>     GetAllConfigAsync();
    Task<string?>                        GetConfigAsync(string key);
    Task                                 SetConfigAsync(string key, string value, int? updatedBy);

    Task<List<GroupMailConfig>>          GetGroupMailConfigsAsync();
    Task<GroupMailConfig?>               GetGroupMailConfigAsync(int groupId);
    Task                                 SaveGroupMailConfigAsync(GroupMailConfig cfg);
}

public partial class DbService : IDbService
{
    // --- SiteConfiguration key/value access ---
    public async Task<Dictionary<string, string>> GetAllConfigAsync()
    {
        using var c = Conn();
        var rows = await c.QueryAsync<(string ConfigKey, string ConfigValue)>(
            "SELECT ConfigKey, ISNULL(ConfigValue,'') AS ConfigValue FROM SiteConfiguration");
        return rows.ToDictionary(r => r.ConfigKey, r => r.ConfigValue ?? "");
    }

    public async Task<string?> GetConfigAsync(string key)
    {
        using var c = Conn();
        return await c.QueryFirstOrDefaultAsync<string?>(
            "SELECT ConfigValue FROM SiteConfiguration WHERE ConfigKey=@key", new { key });
    }

    public async Task SetConfigAsync(string key, string value, int? updatedBy)
    {
        using var c = Conn();
        await c.ExecuteAsync(@"
            MERGE SiteConfiguration AS t
            USING (SELECT @key AS ConfigKey) AS s ON t.ConfigKey = s.ConfigKey
            WHEN MATCHED THEN
                UPDATE SET ConfigValue = @value, UpdatedAt = GETDATE(), UpdatedBy = @updatedBy
            WHEN NOT MATCHED THEN
                INSERT (ConfigKey, ConfigValue, UpdatedAt, UpdatedBy)
                VALUES (@key, @value, GETDATE(), @updatedBy);",
            new { key, value, updatedBy });
    }

    // --- Per-group SMTP override CRUD ---
    public async Task<List<GroupMailConfig>> GetGroupMailConfigsAsync()
    {
        using var c = Conn();
        return (await c.QueryAsync<GroupMailConfig>(@"
            SELECT m.*, g.GroupName
            FROM   GroupMailConfig m
            JOIN   TechnicianGroups g ON g.GroupId = m.GroupId")).ToList();
    }

    public async Task<GroupMailConfig?> GetGroupMailConfigAsync(int groupId)
    {
        using var c = Conn();
        return await c.QueryFirstOrDefaultAsync<GroupMailConfig>(@"
            SELECT m.*, g.GroupName
            FROM   GroupMailConfig m
            JOIN   TechnicianGroups g ON g.GroupId = m.GroupId
            WHERE  m.GroupId = @groupId", new { groupId });
    }

    public async Task SaveGroupMailConfigAsync(GroupMailConfig cfg)
    {
        using var c = Conn();
        var exists = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM GroupMailConfig WHERE GroupId=@GroupId", cfg);
        if (exists > 0)
        {
            // Don't overwrite the saved password if the form posted blank.
            await c.ExecuteAsync(@"
                UPDATE GroupMailConfig SET
                    SmtpHost=@SmtpHost, SmtpPort=@SmtpPort, SmtpUser=@SmtpUser,
                    SmtpFromEmail=@SmtpFromEmail, SmtpFromName=@SmtpFromName,
                    SmtpEnableSsl=@SmtpEnableSsl, IsEnabled=@IsEnabled,
                    UpdatedAt=GETDATE()
                WHERE GroupId=@GroupId", cfg);
            if (!string.IsNullOrEmpty(cfg.SmtpPassword))
            {
                await c.ExecuteAsync(
                    "UPDATE GroupMailConfig SET SmtpPassword=@SmtpPassword WHERE GroupId=@GroupId",
                    cfg);
            }
        }
        else
        {
            await c.ExecuteAsync(@"
                INSERT INTO GroupMailConfig
                    (GroupId,SmtpHost,SmtpPort,SmtpUser,SmtpPassword,SmtpFromEmail,
                     SmtpFromName,SmtpEnableSsl,IsEnabled,UpdatedAt)
                VALUES
                    (@GroupId,@SmtpHost,@SmtpPort,@SmtpUser,@SmtpPassword,@SmtpFromEmail,
                     @SmtpFromName,@SmtpEnableSsl,@IsEnabled,GETDATE())", cfg);
        }
    }

    private SqlConnection Conn() =>
        new SqlConnection(/* your connection string here */);
}
