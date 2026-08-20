using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The dedicated permission-change log (M21). Every entry is already
/// human-readable when it is written — "QcOperator, Arrivals.Details, Read →
/// Edit" — so the reading side never has to diff JSON.
///
/// This sits alongside <see cref="IAuditService"/>, which still records the
/// same events in its generic form. The split exists because the audit log's
/// one-row-per-save shape cannot express "which of these 200 permissions
/// actually moved", and that is the only question anyone asks of it.
/// </summary>
public interface IPermissionLogService
{
    /// <summary>Writes entries inside a caller's transaction, so the log commits
    /// atomically with the permission change it describes. Writing nothing when
    /// nothing changed is deliberate — a no-op save must not create noise.</summary>
    Task WriteAsync(SqlConnection conn, SqlTransaction tx, IEnumerable<PermissionLogEntry> entries);

    /// <summary>Self-contained overload for callers with no ambient transaction
    /// (the Users screen). Best-effort: a logging failure must not roll back a
    /// permission change that already committed, so it is caught and logged.</summary>
    Task WriteAsync(IEnumerable<PermissionLogEntry> entries);

    Task<IReadOnlyList<PermissionLogEntry>> ListAsync(PermissionLogFilter filter);

    /// <summary>Distinct subjects and actors present in the log, for the filter
    /// dropdowns — so an option can never return an empty list.</summary>
    Task<PermissionLogOptions> GetOptionsAsync();
}

public class PermissionLogService : IPermissionLogService
{
    private readonly string _cs;
    private readonly IAuditContext _ctx;
    private readonly ILogger<PermissionLogService> _log;

    public PermissionLogService(IConfiguration cfg, IAuditContext ctx, ILogger<PermissionLogService> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _ctx = ctx; _log = log;
    }

    private const string InsertSql = @"
        INSERT INTO qms_permission_log
            (changed_at, changed_by, subject_type, subject_key, subject_name,
             change_type, item_code, item_name, old_value, new_value, source_ip)
        VALUES
            (SYSUTCDATETIME(), @ChangedBy, @SubjectType, @SubjectKey, @SubjectName,
             @ChangeType, @ItemCode, @ItemName, @OldValue, @NewValue, @SourceIp)";

    private object ToParam(PermissionLogEntry e) => new
    {
        e.ChangedBy, e.SubjectType, e.SubjectKey, e.SubjectName,
        e.ChangeType, e.ItemCode, e.ItemName, e.OldValue, e.NewValue,
        SourceIp = _ctx.RemoteIp
    };

    public async Task WriteAsync(SqlConnection conn, SqlTransaction tx, IEnumerable<PermissionLogEntry> entries)
    {
        var rows = entries.ToList();
        if (rows.Count == 0) return;
        await conn.ExecuteAsync(InsertSql, rows.Select(ToParam), tx);
    }

    public async Task WriteAsync(IEnumerable<PermissionLogEntry> entries)
    {
        var rows = entries.ToList();
        if (rows.Count == 0) return;
        try
        {
            using var c = new SqlConnection(_cs);
            await c.ExecuteAsync(InsertSql, rows.Select(ToParam));
        }
        catch (Exception ex)
        {
            // The permission change itself has already committed. Losing the log
            // row is bad, but undoing an access change the admin believes they
            // made would be worse.
            _log.LogError(ex, "Permission log write failed for {Count} entrie(s)", rows.Count);
        }
    }

    public async Task<IReadOnlyList<PermissionLogEntry>> ListAsync(PermissionLogFilter f)
    {
        var take = Math.Clamp(f.PageSize <= 0 ? 100 : f.PageSize, 1, 500);

        // Dates arrive as the user's LOCAL day; the column is UTC. Converting
        // here rather than comparing naively is what stops rows near midnight
        // falling outside a range that visibly contains them.
        DateTime? fromUtc = f.From.HasValue
            ? DateTime.SpecifyKind(f.From.Value.Date, DateTimeKind.Local).ToUniversalTime()
            : null;
        DateTime? toUtc = f.To.HasValue
            ? DateTime.SpecifyKind(f.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            : null;

        using var c = new SqlConnection(_cs);
        var rows = await c.QueryAsync<PermissionLogEntry>(@"
            SELECT TOP (@take)
                   log_id       AS LogId,
                   changed_at   AS ChangedAt,
                   changed_by   AS ChangedBy,
                   subject_type AS SubjectType,
                   subject_key  AS SubjectKey,
                   subject_name AS SubjectName,
                   change_type  AS ChangeType,
                   item_code    AS ItemCode,
                   item_name    AS ItemName,
                   old_value    AS OldValue,
                   new_value    AS NewValue,
                   source_ip    AS SourceIp
            FROM   qms_permission_log
            WHERE  (@actor       IS NULL OR changed_by   = @actor)
              AND  (@subjectKey  IS NULL OR subject_key  = @subjectKey)
              AND  (@subjectType IS NULL OR subject_type = @subjectType)
              AND  (@changeType  IS NULL OR change_type  = @changeType)
              AND  (@search      IS NULL OR
                    subject_key  LIKE '%' + @search + '%' OR
                    subject_name LIKE '%' + @search + '%' OR
                    item_code    LIKE '%' + @search + '%' OR
                    item_name    LIKE '%' + @search + '%' OR
                    changed_by   LIKE '%' + @search + '%')
              AND  (@fromUtc IS NULL OR changed_at >= @fromUtc)
              AND  (@toUtc   IS NULL OR changed_at <  @toUtc)
            ORDER  BY changed_at DESC, log_id DESC",
            new
            {
                take,
                actor       = Blank(f.Actor),
                subjectKey  = Blank(f.SubjectKey),
                subjectType = Blank(f.SubjectType),
                changeType  = Blank(f.ChangeType),
                search      = Blank(f.Search),
                fromUtc,
                toUtc
            });
        return rows.ToList();

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public async Task<PermissionLogOptions> GetOptionsAsync()
    {
        using var c = new SqlConnection(_cs);
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT changed_by FROM qms_permission_log ORDER BY changed_by;
            -- Aliased: Dapper maps a positional record by constructor parameter
            -- NAME and does not strip underscores, so bare snake_case columns
            -- fail to materialise PermissionLogSubject.
            SELECT DISTINCT subject_key AS SubjectKey, subject_type AS SubjectType,
                            subject_name AS SubjectName
            FROM   qms_permission_log ORDER BY subject_type, subject_key;
            SELECT DISTINCT change_type FROM qms_permission_log ORDER BY change_type;");

        return new PermissionLogOptions
        {
            Actors      = (await grid.ReadAsync<string>()).ToList(),
            Subjects    = (await grid.ReadAsync<PermissionLogSubject>()).ToList(),
            ChangeTypes = (await grid.ReadAsync<string>()).ToList()
        };
    }
}
