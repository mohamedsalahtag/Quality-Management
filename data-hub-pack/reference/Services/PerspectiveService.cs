using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models.Reports;

namespace SharbatlyQMS.Web.Services.Reports;

public class PerspectiveService : IPerspectiveService
{
    private readonly string _cs;

    public PerspectiveService(IConfiguration config)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
    }

    private SqlConnection Open() => new(_cs);

    public async Task<IReadOnlyList<PerspectiveDto>> ListForUserAsync(string reportKey,
        string username, CancellationToken ct)
    {
        // Own (private + own-shared) + everyone else's shared. Ordering:
        // default first, then own, then shared, then by name.
        const string sql = @"
            SELECT perspective_id  AS Id,
                   name            AS Name,
                   scope           AS Scope,
                   is_default      AS IsDefault,
                   CASE WHEN owner_username = @u THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsOwn,
                   owner_username  AS OwnerUsername,
                   config_json     AS ConfigJson,
                   created_at      AS CreatedAt,
                   updated_at      AS UpdatedAt
            FROM   dbo.qms_perspective
            WHERE  report_key = @r
              AND  (owner_username = @u OR scope = 'shared')
            ORDER  BY
                CASE WHEN is_default = 1 AND owner_username = @u THEN 0 ELSE 1 END,
                CASE WHEN owner_username = @u THEN 0 ELSE 1 END,
                name;";
        using var c = Open();
        await c.OpenAsync(ct);
        var rows = await c.QueryAsync<PerspectiveDto>(new CommandDefinition(
            sql, new { r = reportKey, u = username }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<PerspectiveDto?> GetAsync(long id, string username, CancellationToken ct)
    {
        const string sql = @"
            SELECT perspective_id  AS Id,
                   name            AS Name,
                   scope           AS Scope,
                   is_default      AS IsDefault,
                   CASE WHEN owner_username = @u THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END AS IsOwn,
                   owner_username  AS OwnerUsername,
                   config_json     AS ConfigJson,
                   created_at      AS CreatedAt,
                   updated_at      AS UpdatedAt
            FROM   dbo.qms_perspective
            WHERE  perspective_id = @id
              AND  (owner_username = @u OR scope = 'shared');";
        using var c = Open();
        await c.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<PerspectiveDto>(new CommandDefinition(
            sql, new { id, u = username }, cancellationToken: ct));
    }

    public async Task<PerspectiveDto> SaveAsync(PerspectiveSaveRequest req, string username,
        bool canShare, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ReportKey))
            throw new ArgumentException("ReportKey is required", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("Name is required", nameof(req));
        if (string.IsNullOrWhiteSpace(req.ConfigJson))
            throw new ArgumentException("ConfigJson is required", nameof(req));

        // Defensive re-check: non-managers can never persist 'shared'.
        var scope = (req.Scope == PerspectiveScopes.Shared && canShare)
            ? PerspectiveScopes.Shared
            : PerspectiveScopes.Private;

        using var c = Open();
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();

        long id;
        if (req.Id.HasValue)
        {
            // Update: enforce ownership. SiteAdmin bypass for shared admin
            // edits is NOT supported here -- editing someone else's shared
            // perspective requires deleting + re-saving as a new one.
            const string upd = @"
                UPDATE dbo.qms_perspective
                SET    name        = @name,
                       scope       = @scope,
                       config_json = @cfg,
                       updated_at  = SYSUTCDATETIME(),
                       updated_by  = @u
                WHERE  perspective_id = @id
                  AND  owner_username = @u;";
            var n = await c.ExecuteAsync(new CommandDefinition(upd,
                new { id = req.Id, name = req.Name, scope, cfg = req.ConfigJson, u = username },
                tx, cancellationToken: ct));
            if (n == 0)
                throw new UnauthorizedAccessException("Not your perspective or not found.");
            id = req.Id.Value;
        }
        else
        {
            const string ins = @"
                INSERT INTO dbo.qms_perspective
                    (report_key, name, owner_username, scope, is_default, config_json,
                     created_at, created_by)
                OUTPUT INSERTED.perspective_id
                VALUES (@r, @name, @u, @scope, 0, @cfg, SYSUTCDATETIME(), @u);";
            id = await c.ExecuteScalarAsync<long>(new CommandDefinition(ins,
                new { r = req.ReportKey, name = req.Name, scope, cfg = req.ConfigJson, u = username },
                tx, cancellationToken: ct));
        }

        // Optional default toggle, scoped to the caller + report.
        if (req.IsDefault)
        {
            const string clearDefault = @"
                UPDATE dbo.qms_perspective
                SET    is_default = 0
                WHERE  owner_username = @u
                  AND  report_key     = @r
                  AND  perspective_id <> @id;";
            await c.ExecuteAsync(new CommandDefinition(clearDefault,
                new { u = username, r = req.ReportKey, id }, tx, cancellationToken: ct));
            const string setDefault = @"
                UPDATE dbo.qms_perspective
                SET    is_default = 1
                WHERE  perspective_id = @id
                  AND  owner_username = @u;";
            await c.ExecuteAsync(new CommandDefinition(setDefault,
                new { id, u = username }, tx, cancellationToken: ct));
        }

        tx.Commit();

        var dto = await GetAsync(id, username, ct);
        return dto!;
    }

    public async Task<bool> DeleteAsync(long id, string username, bool isSiteAdmin,
        CancellationToken ct)
    {
        // Owner can delete their own (private or shared). SiteAdmin can delete
        // any. Manager cannot delete other managers' shared perspectives --
        // gives shared content a sensible governance gate.
        var sql = isSiteAdmin
            ? "DELETE FROM dbo.qms_perspective WHERE perspective_id = @id;"
            : "DELETE FROM dbo.qms_perspective WHERE perspective_id = @id AND owner_username = @u;";
        using var c = Open();
        await c.OpenAsync(ct);
        var n = await c.ExecuteAsync(new CommandDefinition(sql,
            new { id, u = username }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<bool> SetDefaultAsync(long id, string username, CancellationToken ct)
    {
        // Default must be on a perspective the caller can see (own private or
        // any shared) -- but is_default is per-user, so the row's existence
        // check goes through GetAsync's scope filter.
        const string lookupSql = @"
            SELECT report_key, owner_username, scope
            FROM   dbo.qms_perspective
            WHERE  perspective_id = @id;";

        using var c = Open();
        await c.OpenAsync(ct);
        var row = await c.QuerySingleOrDefaultAsync<(string ReportKey, string Owner, string Scope)>(
            new CommandDefinition(lookupSql, new { id }, cancellationToken: ct));
        if (row.ReportKey == null) return false;
        if (row.Owner != username && row.Scope != PerspectiveScopes.Shared)
            return false;

        using var tx = c.BeginTransaction();
        // Clear my other defaults for this report.
        const string clearDefault = @"
            UPDATE dbo.qms_perspective
            SET    is_default = 0
            WHERE  owner_username = @u
              AND  report_key     = @r
              AND  is_default     = 1;";
        await c.ExecuteAsync(new CommandDefinition(clearDefault,
            new { u = username, r = row.ReportKey }, tx, cancellationToken: ct));

        if (row.Owner == username)
        {
            // I own this row -- flip is_default directly.
            const string setDefault = @"
                UPDATE dbo.qms_perspective
                SET    is_default = 1
                WHERE  perspective_id = @id;";
            await c.ExecuteAsync(new CommandDefinition(setDefault,
                new { id }, tx, cancellationToken: ct));
        }
        else
        {
            // Shared perspective owned by someone else. Per the schema,
            // is_default is keyed on owner -- we cannot flip the original
            // row without losing the original owner's default. Instead,
            // clone it as a private copy owned by the caller (marked default)
            // so the user gets the bookmark behaviour they expect.
            const string clone = @"
                INSERT INTO dbo.qms_perspective
                    (report_key, name, owner_username, scope, is_default,
                     config_json, created_at, created_by)
                SELECT report_key,
                       LEFT(N'Default: ' + name, 150),
                       @u,
                       'private',
                       1,
                       config_json,
                       SYSUTCDATETIME(),
                       @u
                FROM   dbo.qms_perspective
                WHERE  perspective_id = @id;";
            await c.ExecuteAsync(new CommandDefinition(clone,
                new { id, u = username }, tx, cancellationToken: ct));
        }
        tx.Commit();
        return true;
    }
}
