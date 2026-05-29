using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Dapper-based implementation of <see cref="IAuditService"/>. Single
/// write path to qms_audit_log; reads serve both the per-record audit
/// panel and (in later phases) the global list + Excel export.
/// </summary>
public class AuditService : IAuditService
{
    private readonly string _cs;
    private readonly IAuditContext _ctx;

    public AuditService(IConfiguration config, IAuditContext ctx)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _ctx = ctx;
    }

    private SqlConnection Open() => new(_cs);

    // ---- WriteAsync (audit + mutation share the caller's transaction) ----

    public async Task WriteAsync(SqlConnection conn, SqlTransaction tx,
        string entityType, long entityId, string actionCode,
        object? oldValues, object? newValues, string actor)
    {
        // FR-016: skip no-op updates -- if both sides serialise identically
        // (or both are null), there's nothing to log.
        var oldJson = oldValues == null ? null : JsonSerializer.Serialize(oldValues);
        var newJson = newValues == null ? null : JsonSerializer.Serialize(newValues);
        if (actionCode == ActionCodes.Updated && oldJson == newJson) return;

        await conn.ExecuteAsync(@"
            INSERT INTO qms_audit_log
                (entity_type, entity_id, action_code, old_values_json, new_values_json,
                 changed_at, changed_by, source_ip, source_user_agent, source_device_name)
            VALUES
                (@entityType, @entityId, @actionCode, @oldJson, @newJson,
                 SYSUTCDATETIME(), @actor, @ip, @ua, @device)",
            new
            {
                entityType,
                entityId,
                actionCode,
                oldJson,
                newJson,
                actor,
                ip     = (object?)_ctx.RemoteIp   ?? DBNull.Value,
                ua     = (object?)_ctx.UserAgent  ?? DBNull.Value,
                device = (object?)_ctx.DeviceName ?? DBNull.Value
            },
            transaction: tx);
    }

    // ---- GetForRecordAsync (per-record history panel, FR-008) ----

    public async Task<IReadOnlyList<AuditEntryListRow>> GetForRecordAsync(string entityType, long entityId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<AuditEntryListRow>(@"
            SELECT audit_id          AS AuditId,
                   entity_type       AS EntityType,
                   entity_id         AS EntityId,
                   action_code       AS ActionCode,
                   old_values_json   AS OldValuesJson,
                   new_values_json   AS NewValuesJson,
                   changed_at        AS ChangedAt,
                   changed_by        AS ChangedBy,
                   source_ip         AS SourceIp,
                   source_user_agent  AS SourceUserAgent,
                   source_device_name AS SourceDeviceName
            FROM   qms_audit_log
            WHERE  entity_type = @entityType
              AND  entity_id   = @entityId
            ORDER  BY changed_at DESC, audit_id DESC",
            new { entityType, entityId });

        var list = rows.ToList();
        foreach (var r in list)
            r.DiffRows = ParseDiffs(r.OldValuesJson, r.NewValuesJson);
        return list;
    }

    // ---- GetForCompositeRecordAsync (aggregated per-record panel) ----
    //
    // Background: audit rows are written per concrete entity_type
    // (Sample, SampleReading, SampleDefect, QualityOrderMaterial, etc.),
    // not per the parent record the user is looking at. A QO that's
    // never been Opened/Closed but has had its samples edited would
    // therefore show an empty per-record panel under
    // GetForRecordAsync('QualityOrder', qoId) -- which is exactly what
    // the user reported on 2026-05-25.
    //
    // Fix: load the parent's direct children up front, then issue one
    // multi-clause SELECT against qms_audit_log so the panel includes
    // every audit row that touches the record OR any of its children.
    public async Task<IReadOnlyList<AuditEntryListRow>> GetForCompositeRecordAsync(string entityType, long entityId)
    {
        using var c = Open();

        var clauses = new List<string>();
        var p = new DynamicParameters();

        // The parent record itself is always included.
        clauses.Add("(entity_type = @primaryType AND entity_id = @primaryId)");
        p.Add("primaryType", entityType);
        p.Add("primaryId",   entityId);

        switch (entityType)
        {
            case EntityTypes.QualityOrder:
            {
                // Material lines (size overrides, etc.).
                var matIds = (await c.QueryAsync<long>(@"
                    SELECT qo_material_id FROM qms_quality_order_material
                    WHERE  quality_order_id = @id",
                    new { id = entityId })).ToArray();
                if (matIds.Length > 0)
                {
                    clauses.Add("(entity_type = 'QualityOrderMaterial' AND entity_id IN @matIds)");
                    p.Add("matIds", matIds);
                }
                // Samples + their readings, defects, header-value batches.
                // All three child types are written with entity_id = sample_id
                // (one batched audit row per save), so a single IN @sampleIds
                // catches them all.
                var sampleIds = (await c.QueryAsync<long>(@"
                    SELECT sample_id FROM qms_sample WHERE quality_order_id = @id",
                    new { id = entityId })).ToArray();
                if (sampleIds.Length > 0)
                {
                    clauses.Add("(entity_type IN ('Sample','SampleReading','SampleDefect') AND entity_id IN @sampleIds)");
                    p.Add("sampleIds", sampleIds);
                }
                break;
            }
            case EntityTypes.Arrival:
            {
                // ArrivalChecklist rows are written with entity_id = arrival_id
                // (the checklist is 1:1 with the arrival).
                clauses.Add("(entity_type = 'ArrivalChecklist' AND entity_id = @primaryId)");

                // Arrival items (PO line snapshots).
                var itemIds = (await c.QueryAsync<long>(@"
                    SELECT arrival_item_id FROM qms_arrival_item WHERE arrival_id = @id",
                    new { id = entityId })).ToArray();
                if (itemIds.Length > 0)
                {
                    clauses.Add("(entity_type = 'ArrivalItem' AND entity_id IN @itemIds)");
                    p.Add("itemIds", itemIds);
                }
                break;
            }
            case EntityTypes.Claim:
            {
                // ClaimNote rows are written with entity_id = claim_note_id.
                var noteIds = (await c.QueryAsync<long>(@"
                    SELECT claim_note_id FROM qms_claim_note WHERE claim_id = @id",
                    new { id = entityId })).ToArray();
                if (noteIds.Length > 0)
                {
                    clauses.Add("(entity_type = 'ClaimNote' AND entity_id IN @noteIds)");
                    p.Add("noteIds", noteIds);
                }
                break;
            }
            // Other entity types have no aggregation rule -- fall through
            // to the single-record query (the primary clause alone).
        }

        var sql = $@"
            SELECT audit_id          AS AuditId,
                   entity_type       AS EntityType,
                   entity_id         AS EntityId,
                   action_code       AS ActionCode,
                   old_values_json   AS OldValuesJson,
                   new_values_json   AS NewValuesJson,
                   changed_at        AS ChangedAt,
                   changed_by        AS ChangedBy,
                   source_ip         AS SourceIp,
                   source_user_agent  AS SourceUserAgent,
                   source_device_name AS SourceDeviceName
            FROM   qms_audit_log
            WHERE  {string.Join(" OR ", clauses)}
            ORDER  BY changed_at DESC, audit_id DESC";

        var rows = (await c.QueryAsync<AuditEntryListRow>(sql, p)).ToList();
        foreach (var r in rows)
            r.DiffRows = ParseDiffs(r.OldValuesJson, r.NewValuesJson);
        return rows;
    }

    // ---- ListAsync / ExportAsync are filled in Phase 4 / Phase 5 ----

    public async Task<IReadOnlyList<AuditEntryListRow>> ListAsync(AuditFilter filter)
    {
        // Keyset pagination via IX_qms_audit_log_filter for SC-008 (1s p95 at 10M rows).
        // Page size is clamped server-side to 100 regardless of query string.
        var pageSize = filter.PageSize <= 0 ? 25 : Math.Min(filter.PageSize, 100);

        // Build WHERE clauses dynamically -- each multi-value list uses Dapper's
        // IN @parameter expansion, which sends one parameter per item.
        var where = new List<string>();
        var p = new DynamicParameters();

        // The textbox accepts a comma-separated list of usernames -- split here
        // (model binding gives us a single-element array containing the raw
        // string). Then sanitise every list to drop null / whitespace entries,
        // which would otherwise blow up Dapper's IN expansion with
        // "The first item in a list-expansion cannot be null".
        var users       = SplitAndClean(filter.Users, splitComma: true);
        var entityTypes = SplitAndClean(filter.EntityTypes, splitComma: false);
        var actionCodes = SplitAndClean(filter.ActionCodes, splitComma: false);

        if (users.Length > 0)
        {
            where.Add("changed_by IN @users");
            p.Add("users", users);
        }
        if (filter.FromUtc.HasValue)
        {
            where.Add("changed_at >= @fromUtc");
            p.Add("fromUtc", filter.FromUtc.Value);
        }
        if (filter.ToUtc.HasValue)
        {
            // Include the whole "to" day -- 2026-05-21 means "<= 2026-05-21 23:59:59".
            where.Add("changed_at <= @toUtc");
            p.Add("toUtc", filter.ToUtc.Value.Date.AddDays(1).AddTicks(-1));
        }
        if (entityTypes.Length > 0)
        {
            where.Add("entity_type IN @entityTypes");
            p.Add("entityTypes", entityTypes);
        }
        if (actionCodes.Length > 0)
        {
            where.Add("action_code IN @actionCodes");
            p.Add("actionCodes", actionCodes);
        }
        // Keyset cursor -- composite tuple comparison so ordering is stable.
        if (filter.CursorTime.HasValue && filter.CursorId.HasValue)
        {
            where.Add("(changed_at < @cursorTime OR (changed_at = @cursorTime AND audit_id < @cursorId))");
            p.Add("cursorTime", filter.CursorTime.Value);
            p.Add("cursorId",   filter.CursorId.Value);
        }

        var whereClause = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        // Fetch one extra row to detect HasMore in the caller without a separate
        // COUNT(*) query; trimmed back to pageSize before returning.
        p.Add("take", pageSize + 1);

        var sql = $@"
            SELECT TOP (@take)
                   audit_id          AS AuditId,
                   entity_type       AS EntityType,
                   entity_id         AS EntityId,
                   action_code       AS ActionCode,
                   old_values_json   AS OldValuesJson,
                   new_values_json   AS NewValuesJson,
                   changed_at        AS ChangedAt,
                   changed_by        AS ChangedBy,
                   source_ip         AS SourceIp,
                   source_user_agent  AS SourceUserAgent,
                   source_device_name AS SourceDeviceName
            FROM   qms_audit_log
            {whereClause}
            ORDER  BY changed_at DESC, audit_id DESC";

        using var c = Open();
        var rows = (await c.QueryAsync<AuditEntryListRow>(sql, p)).ToList();
        foreach (var r in rows)
            r.DiffRows = ParseDiffs(r.OldValuesJson, r.NewValuesJson);
        return rows;
    }

    public async IAsyncEnumerable<AuditEntry> ExportAsync(
        AuditFilter filter,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Streams rows in chunks so we never buffer the full export in
        // memory. ChangedAt + AuditId are the keyset cursor; we advance
        // the cursor after each chunk until a short page tells us we've
        // reached the tail. PageSize defaults to 1000 for export (versus
        // 25 for the on-screen list).
        var chunk = 1000;
        DateTime? cursorTime = filter.CursorTime;
        long?     cursorId   = filter.CursorId;

        while (!ct.IsCancellationRequested)
        {
            var where = new List<string>();
            var p = new DynamicParameters();

            var users       = SplitAndClean(filter.Users, splitComma: true);
            var entityTypes = SplitAndClean(filter.EntityTypes, splitComma: false);
            var actionCodes = SplitAndClean(filter.ActionCodes, splitComma: false);

            if (users.Length > 0)
            {
                where.Add("changed_by IN @users");
                p.Add("users", users);
            }
            if (filter.FromUtc.HasValue)
            {
                where.Add("changed_at >= @fromUtc");
                p.Add("fromUtc", filter.FromUtc.Value);
            }
            if (filter.ToUtc.HasValue)
            {
                where.Add("changed_at <= @toUtc");
                p.Add("toUtc", filter.ToUtc.Value.Date.AddDays(1).AddTicks(-1));
            }
            if (entityTypes.Length > 0)
            {
                where.Add("entity_type IN @entityTypes");
                p.Add("entityTypes", entityTypes);
            }
            if (actionCodes.Length > 0)
            {
                where.Add("action_code IN @actionCodes");
                p.Add("actionCodes", actionCodes);
            }
            if (cursorTime.HasValue && cursorId.HasValue)
            {
                where.Add("(changed_at < @cursorTime OR (changed_at = @cursorTime AND audit_id < @cursorId))");
                p.Add("cursorTime", cursorTime.Value);
                p.Add("cursorId",   cursorId.Value);
            }
            p.Add("take", chunk);

            var sql = $@"
                SELECT TOP (@take)
                       audit_id          AS AuditId,
                       entity_type       AS EntityType,
                       entity_id         AS EntityId,
                       action_code       AS ActionCode,
                       old_values_json   AS OldValuesJson,
                       new_values_json   AS NewValuesJson,
                       changed_at        AS ChangedAt,
                       changed_by        AS ChangedBy,
                       source_ip         AS SourceIp,
                       source_user_agent  AS SourceUserAgent,
                   source_device_name AS SourceDeviceName
                FROM   qms_audit_log
                {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))}
                ORDER  BY changed_at DESC, audit_id DESC";

            using var c = Open();
            var page = (await c.QueryAsync<AuditEntry>(sql, p)).ToList();
            if (page.Count == 0) yield break;

            foreach (var row in page)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return row;
            }
            if (page.Count < chunk) yield break;

            var last = page[^1];
            cursorTime = last.ChangedAt;
            cursorId   = last.AuditId;
        }
    }

    // ---- Device-label parsing -----------------------------------------

    /// <summary>
    /// Best-effort User-Agent → "Browser / OS" label for the Audit Log
    /// "Device" column. Pattern-matches the most common browsers (Edge,
    /// Chrome, Firefox, Safari) and OSes (Windows, macOS, iOS, Android,
    /// Linux). Returns "unknown" when the UA is missing or doesn't match
    /// any known pattern -- the raw UA is still shown to the SiteAdmin
    /// via the tooltip in the view.
    ///
    /// Order matters: Edge / Opera identify themselves as Chrome too, so
    /// we check those first. iOS identifies as Safari with "iPhone OS".
    /// </summary>
    public static string DeviceLabel(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "unknown";
        var ua = userAgent;

        // Browser
        string browser =
              ua.Contains("Edg/",      StringComparison.Ordinal)        ? "Edge"
            : ua.Contains("OPR/",      StringComparison.Ordinal)        ? "Opera"
            : ua.Contains("Firefox/",  StringComparison.Ordinal)        ? "Firefox"
            : ua.Contains("Chrome/",   StringComparison.Ordinal)        ? "Chrome"
            : ua.Contains("Safari/",   StringComparison.Ordinal)
              && !ua.Contains("Chrome/", StringComparison.Ordinal)      ? "Safari"
            : ua.Contains("MSIE",      StringComparison.Ordinal)
              || ua.Contains("Trident/", StringComparison.Ordinal)      ? "IE"
            : "Browser";

        // OS / device family
        string os =
              ua.Contains("Windows NT 10",   StringComparison.Ordinal)  ? "Windows"
            : ua.Contains("Windows",         StringComparison.Ordinal)  ? "Windows"
            : ua.Contains("iPhone",          StringComparison.Ordinal)  ? "iPhone"
            : ua.Contains("iPad",            StringComparison.Ordinal)  ? "iPad"
            : ua.Contains("Android",         StringComparison.Ordinal)  ? "Android"
            : ua.Contains("Mac OS X",        StringComparison.Ordinal)
              || ua.Contains("Macintosh",    StringComparison.Ordinal)  ? "macOS"
            : ua.Contains("Linux",           StringComparison.Ordinal)  ? "Linux"
            : ua.Contains("CrOS",            StringComparison.Ordinal)  ? "ChromeOS"
            : "Unknown OS";

        return $"{browser} / {os}";
    }

    // ---- Filter sanitisation ------------------------------------------

    /// <summary>
    /// Normalises a list of filter values coming off the URL query string:
    /// drops null/whitespace entries, optionally splits each entry on
    /// commas (used for the free-text "users" textbox). Prevents Dapper's
    /// `IN @list` from blowing up on null elements when the form is
    /// submitted with empty inputs.
    /// </summary>
    private static string[] SplitAndClean(string[]? input, bool splitComma)
    {
        if (input == null || input.Length == 0) return Array.Empty<string>();
        IEnumerable<string?> seq = input;
        if (splitComma)
        {
            seq = input.SelectMany(s =>
                string.IsNullOrEmpty(s)
                    ? Array.Empty<string>()
                    : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return seq
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ---- Diff parsing -------------------------------------------------

    /// <summary>
    /// Parses old + new JSON snapshots into a flat list of field-level
    /// diffs. Each side is treated as a single-level object; nested
    /// objects are stringified by their JSON form. Fields that didn't
    /// change are omitted (FR-022 -- only changed fields appear in the
    /// UI). Reused by GetForRecordAsync and (later) ListAsync.
    /// </summary>
    internal static IReadOnlyList<DiffRow> ParseDiffs(string? oldJson, string? newJson)
    {
        var oldFields = ParseFlat(oldJson);
        var newFields = ParseFlat(newJson);
        if (oldFields.Count == 0 && newFields.Count == 0) return Array.Empty<DiffRow>();

        var allKeys = new SortedSet<string>(oldFields.Keys);
        foreach (var k in newFields.Keys) allKeys.Add(k);

        var rows = new List<DiffRow>(allKeys.Count);
        foreach (var key in allKeys)
        {
            oldFields.TryGetValue(key, out var oldVal);
            newFields.TryGetValue(key, out var newVal);
            // Only emit a row when the values differ (Created and Deleted
            // entries naturally have all-new or all-old, respectively).
            if (!string.Equals(oldVal, newVal, StringComparison.Ordinal))
                rows.Add(new DiffRow(key, oldVal, newVal));
        }
        return rows;
    }

    private static Dictionary<string, string?> ParseFlat(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string?>(0);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string?>(0);
            var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null      => null,
                    JsonValueKind.String    => prop.Value.GetString(),
                    JsonValueKind.True      => "true",
                    JsonValueKind.False     => "false",
                    JsonValueKind.Number    => prop.Value.GetRawText(),
                    _                       => prop.Value.GetRawText()  // object / array -- stringify
                };
            }
            return dict;
        }
        catch (JsonException)
        {
            // Malformed JSON (shouldn't happen — we wrote it ourselves) — log nothing.
            return new Dictionary<string, string?>(0);
        }
    }
}
