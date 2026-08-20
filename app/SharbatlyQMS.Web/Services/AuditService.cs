using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly IMemoryCache _cache;

    public AuditService(IConfiguration config, IAuditContext ctx, IMemoryCache cache)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _ctx = ctx;
        _cache = cache;
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

        await conn.ExecuteAsync(InsertSql,
            BuildParams(entityType, entityId, actionCode, oldJson, newJson, actor),
            transaction: tx);
    }

    public async Task WriteAsync(string entityType, long entityId, string actionCode,
        object? oldValues, object? newValues, string actor)
    {
        var oldJson = oldValues == null ? null : JsonSerializer.Serialize(oldValues);
        var newJson = newValues == null ? null : JsonSerializer.Serialize(newValues);
        if (actionCode == ActionCodes.Updated && oldJson == newJson) return;

        using var c = Open();
        await c.OpenAsync();
        await c.ExecuteAsync(InsertSql,
            BuildParams(entityType, entityId, actionCode, oldJson, newJson, actor));
    }

    private const string InsertSql = @"
            INSERT INTO qms_audit_log
                (entity_type, entity_id, action_code, old_values_json, new_values_json,
                 changed_at, changed_by, source_ip, source_user_agent, source_device_name)
            VALUES
                (@entityType, @entityId, @actionCode, @oldJson, @newJson,
                 SYSUTCDATETIME(), @actor, @ip, @ua, @device)";

    private object BuildParams(string entityType, long entityId, string actionCode,
        string? oldJson, string? newJson, string actor) => new
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
    };

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
                // ClaimNote rows are written with entity_id = qms_claim_note.note_id.
                var noteIds = (await c.QueryAsync<long>(@"
                    SELECT note_id FROM qms_claim_note WHERE claim_id = @id",
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
        // The picked dates are LOCAL days; changed_at is UTC and the table
        // renders local. Converting here is what stops rows either side of the
        // +03:00 day boundary falling outside a range that visibly contains them.
        if (filter.From.HasValue)
        {
            where.Add("changed_at >= @fromUtc");
            p.Add("fromUtc", DateTime.SpecifyKind(filter.From.Value.Date, DateTimeKind.Local).ToUniversalTime());
        }
        if (filter.To.HasValue)
        {
            // Exclusive next-midnight rather than BETWEEN, which would drop
            // everything after 00:00:00.000 on the To date.
            where.Add("changed_at < @toUtc");
            p.Add("toUtc", DateTime.SpecifyKind(filter.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime());
        }
        if (entityTypes.Length > 0)
        {
            where.Add("entity_type IN @entityTypes");
            p.Add("entityTypes", entityTypes);
        }
        else if (!filter.ShowTechnical)
        {
            // Only when the reader has not picked types themselves -- an explicit
            // choice of "Sample" must not be silently overruled.
            where.Add("entity_type NOT IN @noisyTypes");
            p.Add("noisyTypes", AuditNarrator.NoisyEntityTypes);
        }
        if (actionCodes.Length > 0)
        {
            where.Add("action_code IN @actionCodes");
            p.Add("actionCodes", actionCodes);
        }
        var scope = BuildRecordScope(filter, p);
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

        var sql = $@"{scope.Cte}
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
            FROM   qms_audit_log l
            {scope.Join}
            {whereClause}
            ORDER  BY changed_at DESC, audit_id DESC";

        using var c = Open();
        var rows = (await c.QueryAsync<AuditEntryListRow>(sql, p)).ToList();
        foreach (var r in rows)
            r.DiffRows = ParseDiffs(r.OldValuesJson, r.NewValuesJson);
        await ResolveLabelsAsync(c, rows);
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
            if (filter.From.HasValue)
            {
                where.Add("changed_at >= @fromUtc");
                p.Add("fromUtc", DateTime.SpecifyKind(filter.From.Value.Date, DateTimeKind.Local).ToUniversalTime());
            }
            if (filter.To.HasValue)
            {
                where.Add("changed_at <= @toUtc");
                p.Add("toUtc", DateTime.SpecifyKind(filter.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime().AddTicks(-1));
            }
            if (entityTypes.Length > 0)
            {
                where.Add("entity_type IN @entityTypes");
                p.Add("entityTypes", entityTypes);
            }
            else if (!filter.ShowTechnical)
            {
                // Mirrors the on-screen default. Without it the workbook would
                // hold the 89% of bookkeeping rows the list deliberately hid,
                // so what you exported never matched what you were looking at.
                where.Add("entity_type NOT IN @noisyTypes");
                p.Add("noisyTypes", AuditNarrator.NoisyEntityTypes);
            }
            if (actionCodes.Length > 0)
            {
                where.Add("action_code IN @actionCodes");
                p.Add("actionCodes", actionCodes);
            }
            var scope = BuildRecordScope(filter, p);
            if (cursorTime.HasValue && cursorId.HasValue)
            {
                // Keyset pagination on (changed_at DESC, audit_id DESC).
                // The OR/<= split is intentional: rows with the same
                // changed_at as the previous page's tail row are still
                // included on the next page as long as their audit_id is
                // strictly less than the tail row's. Without the second
                // branch, ties on changed_at (sub-millisecond collisions on
                // bulk inserts) would silently drop rows.
                where.Add("(changed_at < @cursorTime OR (changed_at = @cursorTime AND audit_id < @cursorId))");
                p.Add("cursorTime", cursorTime.Value);
                p.Add("cursorId",   cursorId.Value);
            }
            p.Add("take", chunk);

            var sql = $@"{scope.Cte}
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
                FROM   qms_audit_log l
                {scope.Join}
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

    // ---- Record scope: QC number / container / supplier / plant -------------

    /// <summary>
    /// The SQL fragments that narrow the log to one quality order, container,
    /// supplier or plant. Empty when no record filter is set.
    /// </summary>
    private readonly record struct RecordScope(string Cte, string Join)
    {
        public static readonly RecordScope None = new("", "");
    }

    /// <summary>
    /// Builds the "everything that happened to this container" filter.
    ///
    /// This is the filter the log was missing. An audit row records
    /// "SampleReading #91204" — nobody knows that number. People know the QC
    /// number and the container, so the scope walks
    /// arrival → quality order → material / sample / claim and matches any
    /// audit row that lands anywhere on that tree.
    ///
    /// Shaped as a CTE that is JOINED to, rather than a correlated EXISTS in the
    /// WHERE clause. The EXISTS form is the obvious way to write it and is
    /// unusable: SQL Server re-evaluates it per audit row, which measured at
    /// 31s for a container and timed out past 120s for a single QC number.
    /// Materialising the target keys first and letting the join seek
    /// IX_qms_audit_log_entity (entity_type, entity_id) brings the same queries
    /// to ~250ms.
    ///
    /// The LEFT JOIN from the arrival side is deliberate: an arrival that has no
    /// quality order yet still has audit rows, and filtering by container must
    /// find them. Supplying a QC number forces the join to match, because an
    /// arrival without a quality order has no number to compare.
    /// </summary>
    private static RecordScope BuildRecordScope(AuditFilter filter, DynamicParameters p)
    {
        if (!filter.HasRecordScope) return RecordScope.None;

        p.Add("scopeQo",        Blank(filter.QoNo));
        p.Add("scopeContainer", Blank(filter.Container));
        p.Add("scopeVendor",    Blank(filter.Vendor));
        p.Add("scopePlant",     Blank(filter.Plant));

        const string cte = @"
        WITH scope AS (
            SELECT sa.arrival_id, sqo.quality_order_id
            FROM   qms_arrival sa
            LEFT   JOIN qms_quality_order sqo ON sqo.arrival_id = sa.arrival_id
            WHERE  (@scopeQo        IS NULL OR sqo.quality_order_no LIKE '%' + @scopeQo + '%')
              AND  (@scopeContainer IS NULL OR sa.container_no      LIKE '%' + @scopeContainer + '%')
              AND  (@scopeVendor    IS NULL OR sa.vendor_name       LIKE '%' + @scopeVendor + '%')
              AND  (@scopePlant     IS NULL OR sa.plant             =  @scopePlant)
        ),
        -- One row per (entity_type, entity_id) the scope covers. entity_id is
        -- bigint in the log while the source keys are int, so every branch casts
        -- up rather than letting the join convert the indexed column down.
        targets AS (
            SELECT 'QualityOrder' AS et, CAST(quality_order_id AS bigint) AS eid
              FROM scope WHERE quality_order_id IS NOT NULL
            UNION ALL SELECT 'Arrival',              CAST(arrival_id AS bigint) FROM scope
            UNION ALL SELECT 'ArrivalChecklist',     CAST(arrival_id AS bigint) FROM scope
            UNION ALL SELECT 'QualityOrderMaterial', CAST(m.qo_material_id AS bigint)
                        FROM qms_quality_order_material m
                        JOIN scope s ON s.quality_order_id = m.quality_order_id
            UNION ALL SELECT 'Sample',               CAST(sp.sample_id AS bigint)
                        FROM qms_sample sp JOIN scope s ON s.quality_order_id = sp.quality_order_id
            UNION ALL SELECT 'SampleReading',        CAST(sp.sample_id AS bigint)
                        FROM qms_sample sp JOIN scope s ON s.quality_order_id = sp.quality_order_id
            UNION ALL SELECT 'SampleDefect',         CAST(sp.sample_id AS bigint)
                        FROM qms_sample sp JOIN scope s ON s.quality_order_id = sp.quality_order_id
            UNION ALL SELECT 'ArrivalItem',          CAST(ai.arrival_item_id AS bigint)
                        FROM qms_arrival_item ai JOIN scope s ON s.arrival_id = ai.arrival_id
            UNION ALL SELECT 'Claim',                CAST(cl.claim_id AS bigint)
                        FROM qms_claim cl JOIN scope s ON s.quality_order_id = cl.quality_order_id
            UNION ALL SELECT 'ClaimNote',            CAST(cn.note_id AS bigint)
                        FROM qms_claim_note cn
                        JOIN qms_claim cl ON cl.claim_id = cn.claim_id
                        JOIN scope s ON s.quality_order_id = cl.quality_order_id
        )";

        // Sample readings, defects and header values all share the sample's id,
        // so the UNION above repeats keys; DISTINCT stops one audit row being
        // returned several times.
        const string join = @"JOIN (SELECT DISTINCT et, eid FROM targets) t
               ON t.et = l.entity_type AND t.eid = l.entity_id";

        return new RecordScope(cte, join);
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---- Label resolution ---------------------------------------------------

    /// <summary>
    /// Turns each row's (entity_type, entity_id) back into the identifiers a
    /// person recognises — QC number, container, username — so the list can say
    /// "sample 3 of QO-2026-000353" instead of "#91204".
    ///
    /// Batched: one small query per entity family for the whole page, not one
    /// per row. A page is 25 rows, so this is at most a handful of point lookups
    /// against indexed keys.
    /// </summary>
    private async Task ResolveLabelsAsync(SqlConnection c, List<AuditEntryListRow> rows)
    {
        if (rows.Count == 0) return;

        long[] IdsOf(params string[] types) => rows
            .Where(r => types.Contains(r.EntityType, StringComparer.OrdinalIgnoreCase) && r.EntityId > 0)
            .Select(r => r.EntityId).Distinct().ToArray();

        var qoIds      = IdsOf(EntityTypes.QualityOrder);
        var matIds     = IdsOf(EntityTypes.QualityOrderMaterial);
        var sampleIds  = IdsOf(EntityTypes.Sample, EntityTypes.SampleReading, EntityTypes.SampleDefect);
        var arrivalIds = IdsOf(EntityTypes.Arrival, EntityTypes.ArrivalChecklist);
        var itemIds    = IdsOf(EntityTypes.ArrivalItem);
        var claimIds   = IdsOf(EntityTypes.Claim);
        var noteIds    = IdsOf(EntityTypes.ClaimNote);
        var userIds    = IdsOf(EntityTypes.User);

        var qo      = new Dictionary<long, RecordKey>();
        var mat     = new Dictionary<long, RecordKey>();
        var sample  = new Dictionary<long, RecordKey>();
        var arrival = new Dictionary<long, RecordKey>();
        var item    = new Dictionary<long, RecordKey>();
        var claim   = new Dictionary<long, RecordKey>();
        var note    = new Dictionary<long, RecordKey>();
        var users   = new Dictionary<long, string>();

        const string QoCols = @"qo.quality_order_no AS QoNo, a.container_no AS ContainerNo";

        if (qoIds.Length > 0)
            foreach (var r in await c.QueryAsync<RecordKeyRow>($@"
                SELECT qo.quality_order_id AS Id, {QoCols}, NULL AS Extra
                FROM   qms_quality_order qo
                LEFT   JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                WHERE  qo.quality_order_id IN @ids", new { ids = qoIds }))
                qo[r.Id] = new RecordKey(r.QoNo, r.ContainerNo, null);

        if (matIds.Length > 0)
            foreach (var r in await c.QueryAsync<RecordKeyRow>($@"
                SELECT m.qo_material_id AS Id, {QoCols}, m.material_no AS Extra
                FROM   qms_quality_order_material m
                JOIN   qms_quality_order qo ON qo.quality_order_id = m.quality_order_id
                LEFT   JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                WHERE  m.qo_material_id IN @ids", new { ids = matIds }))
                mat[r.Id] = new RecordKey(r.QoNo, r.ContainerNo, r.Extra);

        if (sampleIds.Length > 0)
            foreach (var r in await c.QueryAsync<RecordKeyRow>($@"
                SELECT s.sample_id AS Id, {QoCols}, CAST(s.sample_no AS nvarchar(20)) AS Extra
                FROM   qms_sample s
                JOIN   qms_quality_order qo ON qo.quality_order_id = s.quality_order_id
                LEFT   JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                WHERE  s.sample_id IN @ids", new { ids = sampleIds }))
                sample[r.Id] = new RecordKey(r.QoNo, r.ContainerNo, r.Extra);

        if (arrivalIds.Length > 0)
            foreach (var r in await c.QueryAsync<RecordKeyRow>(@"
                SELECT a.arrival_id AS Id, NULL AS QoNo, a.container_no AS ContainerNo,
                       a.arrival_no AS Extra
                FROM   qms_arrival a WHERE a.arrival_id IN @ids", new { ids = arrivalIds }))
                arrival[r.Id] = new RecordKey(null, r.ContainerNo, r.Extra);

        if (itemIds.Length > 0)
            foreach (var r in await c.QueryAsync<RecordKeyRow>(@"
                SELECT i.arrival_item_id AS Id, NULL AS QoNo, a.container_no AS ContainerNo,
                       a.arrival_no AS Extra
                FROM   qms_arrival_item i
                JOIN   qms_arrival a ON a.arrival_id = i.arrival_id
                WHERE  i.arrival_item_id IN @ids", new { ids = itemIds }))
                item[r.Id] = new RecordKey(null, r.ContainerNo, r.Extra);

        if (claimIds.Length > 0)
            foreach (var r in await c.QueryAsync<RecordKeyRow>($@"
                SELECT cl.claim_id AS Id, {QoCols}, NULL AS Extra
                FROM   qms_claim cl
                JOIN   qms_quality_order qo ON qo.quality_order_id = cl.quality_order_id
                LEFT   JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                WHERE  cl.claim_id IN @ids", new { ids = claimIds }))
                claim[r.Id] = new RecordKey(r.QoNo, r.ContainerNo, null);

        if (noteIds.Length > 0)
            foreach (var r in await c.QueryAsync<RecordKeyRow>($@"
                SELECT cn.note_id AS Id, {QoCols}, NULL AS Extra
                FROM   qms_claim_note cn
                JOIN   qms_claim cl ON cl.claim_id = cn.claim_id
                JOIN   qms_quality_order qo ON qo.quality_order_id = cl.quality_order_id
                LEFT   JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                WHERE  cn.note_id IN @ids", new { ids = noteIds }))
                note[r.Id] = new RecordKey(r.QoNo, r.ContainerNo, null);

        if (userIds.Length > 0)
            foreach (var r in await c.QueryAsync<UserKeyRow>(@"
                SELECT UserId AS Id, Username FROM portal.[User] WHERE UserId IN @ids",
                new { ids = userIds.Select(i => (int)i).ToArray() }))
                users[r.Id] = r.Username;

        foreach (var r in rows)
        {
            RecordKey? k = r.EntityType switch
            {
                EntityTypes.QualityOrder         => Look(qo,      r.EntityId),
                EntityTypes.QualityOrderMaterial => Look(mat,     r.EntityId),
                EntityTypes.Sample or EntityTypes.SampleReading or EntityTypes.SampleDefect
                                                 => Look(sample,  r.EntityId),
                EntityTypes.Arrival or EntityTypes.ArrivalChecklist
                                                 => Look(arrival, r.EntityId),
                EntityTypes.ArrivalItem          => Look(item,    r.EntityId),
                EntityTypes.Claim                => Look(claim,   r.EntityId),
                EntityTypes.ClaimNote            => Look(note,    r.EntityId),
                _                                => null
            };

            if (k != null)
            {
                r.QoNo         = k.QoNo;
                r.ContainerNo  = k.ContainerNo;
                r.DisplayLabel = AuditNarrator.RecordLabel(r.EntityType, k.QoNo, k.ContainerNo, k.Extra);
            }
            else if (r.EntityType == EntityTypes.User && users.TryGetValue(r.EntityId, out var un))
            {
                r.DisplayLabel = AuditNarrator.RecordLabel(EntityTypes.User, null, null, un);
            }
            else if (r.EntityType == EntityTypes.Role)
            {
                // Role rows are written with entity_id 0 — the role code lives in
                // the payload, which is the only place it can be recovered from.
                var roleCode = AuditNarrator.RoleCodeFromJson(r.NewValuesJson ?? r.OldValuesJson);
                r.DisplayLabel = AuditNarrator.RecordLabel(EntityTypes.Role, null, null, roleCode);
            }
        }

        static RecordKey? Look(Dictionary<long, RecordKey> d, long id) =>
            d.TryGetValue(id, out var v) ? v : null;
    }

    private sealed record RecordKey(string? QoNo, string? ContainerNo, string? Extra);

    /// <summary>Dapper materialisation target for the label lookups. A class
    /// rather than a positional record so every query can name its columns in
    /// whatever order reads best.</summary>
    private sealed class RecordKeyRow
    {
        public long    Id          { get; set; }
        public string? QoNo        { get; set; }
        public string? ContainerNo { get; set; }
        public string? Extra       { get; set; }
    }

    private sealed class UserKeyRow
    {
        public long   Id       { get; set; }
        public string Username { get; set; } = "";
    }

    // ---- Filter dropdown contents ------------------------------------------

    /// <summary>
    /// Suppliers, plants and actors that actually occur, for the filter panel.
    /// Drawn from the data rather than a hard-coded list, so a new supplier
    /// appears without a code change and no option can return nothing.
    /// </summary>
    /// <summary>How long the dropdown contents are reused. Three DISTINCT scans
    /// on every page load would be a real cost for a list that gains an entry
    /// when a new supplier ships — minutes of staleness are free here.</summary>
    private static readonly TimeSpan FilterOptionsTtl = TimeSpan.FromMinutes(10);
    private const string FilterOptionsCacheKey = "audit-filter-options";

    public async Task<AuditFilterOptions> GetFilterOptionsAsync()
    {
        if (_cache.TryGetValue<AuditFilterOptions>(FilterOptionsCacheKey, out var cached) && cached != null)
            return cached;

        var options = await LoadFilterOptionsAsync();
        _cache.Set(FilterOptionsCacheKey, options, FilterOptionsTtl);
        return options;
    }

    private async Task<AuditFilterOptions> LoadFilterOptionsAsync()
    {
        using var c = Open();
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT vendor_name FROM qms_arrival
            WHERE  vendor_name IS NOT NULL AND LTRIM(RTRIM(vendor_name)) <> ''
            ORDER  BY vendor_name;

            SELECT DISTINCT plant FROM qms_arrival
            WHERE  plant IS NOT NULL AND LTRIM(RTRIM(plant)) <> ''
            ORDER  BY plant;

            SELECT DISTINCT changed_by FROM qms_audit_log
            WHERE  changed_by IS NOT NULL AND LTRIM(RTRIM(changed_by)) <> ''
            ORDER  BY changed_by;");

        return new AuditFilterOptions
        {
            Vendors = (await grid.ReadAsync<string>()).ToList(),
            Plants  = (await grid.ReadAsync<string>()).ToList(),
            Actors  = (await grid.ReadAsync<string>()).ToList()
        };
    }
}
