using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Reports -> KPI reports: performance per user, from what QMS already
/// records. No tracking of its own.
///
/// Everything is first collapsed into EVENTS -- one row per thing a user did
/// in the window (an order finished, a sample entered, a claim decided), each
/// carrying the order or arrival it belongs to, a quantity and, for timed
/// steps, a duration. The page's figures are aggregates of those events and
/// the drill panel lists them, so a number and the rows behind it cannot
/// disagree.
///
/// Attribution rules:
///   * a step's duration belongs to whoever COMPLETED it (open->submit to the
///     submitter, submit->finish to the finisher);
///   * a send-back belongs to the person who submitted, a reopen or a later
///     reinspection to the person who finished -- the work that was judged;
///   * the window is the date of the action, local time.
/// </summary>
public interface IKpiReportService
{
    Task<KpiDataset> GetAsync(DateTime from, DateTime to, PlantScope scope, CancellationToken ct = default);
    Task<IReadOnlyList<KpiEvent>> DrillAsync(string kpi, string user, DateTime from, DateTime to, PlantScope scope,
                                             CancellationToken ct = default);
}

/// <summary>One KPI as the page shows it.</summary>
public sealed record KpiDef(string Key, string Tab, string Label, string Kind, string Better,
                            string? Num = null, string? Den = null, string? Hint = null);

public sealed class KpiEvent
{
    public string Kpi { get; set; } = "";
    public string User { get; set; } = "";
    public long? QoId { get; set; }
    public string? QoNo { get; set; }
    public long? ArrivalId { get; set; }
    public string? ArrivalNo { get; set; }
    public string? ContainerNo { get; set; }
    public string? Plant { get; set; }
    public decimal Qty { get; set; } = 1;
    /// <summary>Hours, or days for received->opened.</summary>
    public decimal? Dur { get; set; }
    public DateTime At { get; set; }
}

public sealed class KpiUser
{
    public string User { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public List<string> Plants { get; set; } = new();
}

public sealed class KpiDataset
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<KpiDef> Kpis { get; set; } = new();
    public List<KpiUser> Users { get; set; } = new();
    /// <summary>kpi -> user -> [count (or quantity), median, average]; the
    /// last two only for durations.</summary>
    public Dictionary<string, Dictionary<string, decimal?[]>> Values { get; set; } = new();
    public Dictionary<string, string> PlantNames { get; set; } = new();
    public string Generated { get; set; } = "";
}

public sealed class KpiReportService : IKpiReportService
{
    public const int MaxDays = 366;

    /// <summary>Accounts that act but are not people.</summary>
    public static readonly string[] SystemActors = { "system", "test", "qm-user", "catalog-seed" };

    public static readonly IReadOnlyList<KpiDef> Kpis = new KpiDef[]
    {
        // ---- Volume
        new("arrivals_created",   "volume", "Arrivals registered", "count", "high"),
        new("arrivals_completed", "volume", "Arrivals completed",  "count", "high"),
        new("qo_opened",          "volume", "QC orders opened",    "count", "high"),
        new("qo_submitted",       "volume", "Submitted",           "count", "high"),
        new("qo_finished",        "volume", "Finished",            "count", "high"),
        new("samples",            "volume", "Samples entered",     "count", "high"),
        new("cartons",            "volume", "Cartons inspected",   "count", "high"),
        new("photos",             "volume", "Photos",              "count", "high"),
        new("active_days",        "volume", "Active days",         "count", "high", Hint: "Days with at least one saved change."),
        // ---- Speed
        new("recv_open",      "speed", "Received → opened (days)",       "dur", "low", Hint: "Branch receive date to the day the QC order was opened; to the opener."),
        new("open_submit",    "speed", "Opened → submitted (h)",         "dur", "low", Hint: "To the submitter."),
        new("submit_finish",  "speed", "Submitted → finished (h)",       "dur", "low", Hint: "Supervisor review time; to the finisher."),
        new("finish_send",    "speed", "Finished → report e-mailed (h)", "dur", "low", Hint: "First send of each order; to the sender."),
        new("claim_decision", "speed", "Claim raised → decided (h)",     "dur", "low", Hint: "To the claim manager who decided."),
        // ---- Quality of work (rates; lower is better)
        new("sent_back_rate",   "quality", "Sent back %",        "rate", "low", "sent_back", "qo_submitted", "Submissions the supervisor returned (Cancel submit)."),
        new("reopened_rate",    "quality", "Reopened %",         "rate", "low", "reopened", "qo_finished", "Finished orders later reopened."),
        new("reinspected_rate", "quality", "Reinspected %",      "rate", "low", "reinspected", "qo_finished", "Finished orders later sent for reinspection."),
        new("deleted_rate",     "quality", "Samples deleted %",  "rate", "low", "deleted_samples", "samples_all", null),
        new("corrections_rate", "quality", "Readings corrected %", "rate", "low", "corrections", "samples", "Saved readings or defects later changed."),
        new("slips_rate",       "quality", "Weight typing slips %", "rate", "low", "weight_slips", "weight_readings", "Gross/Net weights above 3× or below ⅓ of the carton weight."),
        new("overrides",        "quality", "Size overrides",     "count", "low"),
        // ---- Claims & reporting
        new("claim_requests",  "claims", "Claim requests raised", "count", "none"),
        new("passed_qc",       "claims", "Passed QC",             "count", "none"),
        new("potential_flags", "claims", "Potential claims flagged", "count", "none"),
        new("claims_reviewed", "claims", "Claims reviewed",       "count", "high"),
        new("claims_hold",     "claims", "Claims put on hold",    "count", "none"),
        new("claim_decision_c","claims", "Decision time (h)",     "dur",   "low", Hint: "Claim raised → decided."),
        new("notes",           "claims", "Notes posted",          "count", "high"),
        new("reports_sent",    "claims", "Reports e-mailed",      "count", "high"),
    };

    /// <summary>Counts that only feed rates (numerators / denominators).</summary>
    private static readonly string[] Hidden =
        { "sent_back", "reopened", "reinspected", "deleted_samples", "samples_all", "corrections", "weight_slips", "weight_readings" };

    private readonly string _cs;
    private readonly ICodeDescriptionDirectory _codes;
    private readonly IMemoryCache _cache;

    /// <summary>How long a loaded period is reused -- long enough that the
    /// drill panel opens at once after the page loads, short enough that the
    /// figures are never stale for long.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    public KpiReportService(IConfiguration config, ICodeDescriptionDirectory codes, IMemoryCache cache)
    {
        _cs = config.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _codes = codes;
        _cache = cache;
    }

    // Shared tail: the order / arrival an event belongs to, and plant scope.
    private const string Ref = @"qo.quality_order_id AS QoId, qo.quality_order_no AS QoNo, a.arrival_id AS ArrivalId,
               a.arrival_no AS ArrivalNo, a.container_no AS ContainerNo, a.plant AS Plant";
    private const string InScope = "(@sUnrestricted = 1 OR a.plant IN @sPlants)";

    private const string Sql = @"
        SET NOCOUNT ON;
        -- 1. Quality-order transitions, each with the row before it.
        WITH h AS (
            SELECT h.entity_id, h.old_status, h.new_status, h.changed_at, h.changed_by,
                   LAG(h.changed_by) OVER (PARTITION BY h.entity_id ORDER BY h.changed_at, h.status_history_id) AS prev_by,
                   LAG(h.changed_at) OVER (PARTITION BY h.entity_id ORDER BY h.changed_at, h.status_history_id) AS prev_at,
                   LAG(h.new_status) OVER (PARTITION BY h.entity_id ORDER BY h.changed_at, h.status_history_id) AS prev_status
            FROM   qms_status_history h WHERE h.entity_type = 'QualityOrder')
        SELECT e.Kpi, e.[User], " + Ref + @", 1 AS Qty, e.Dur, h.changed_at AS At
        FROM   h
        JOIN   qms_quality_order qo ON qo.quality_order_id = h.entity_id
        JOIN   qms_arrival a        ON a.arrival_id = qo.arrival_id
        LEFT JOIN qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
        OUTER APPLY (SELECT TOP 1 c2.receive_date FROM qms_sap_container_cache c2
                     WHERE c2.container_no = a.container_no AND c2.bol_no = a.bol_no AND c2.ebeln = a.ebeln
                     ORDER BY c2.doc_date DESC, c2.sto) cc
        CROSS APPLY (VALUES
            (CASE WHEN h.old_status = 'Initial' AND h.new_status = 'Open' THEN 'qo_opened' END, h.changed_by, CAST(NULL AS DECIMAL(18,2))),
            (CASE WHEN h.old_status = 'Initial' AND h.new_status = 'Open'
                       AND COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) IS NOT NULL THEN 'recv_open' END, h.changed_by,
             CAST(DATEDIFF(DAY, COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date),
                           CAST(DATEADD(MINUTE, @tz, h.changed_at) AS DATE)) AS DECIMAL(18,2))),
            (CASE WHEN h.new_status = 'Submitted' THEN 'qo_submitted' END, h.changed_by, NULL),
            (CASE WHEN h.new_status = 'Submitted' AND h.prev_status = 'Open' THEN 'open_submit' END, h.changed_by,
             CAST(DATEDIFF(MINUTE, h.prev_at, h.changed_at) / 60.0 AS DECIMAL(18,2))),
            (CASE WHEN h.new_status = 'Closed' THEN 'qo_finished' END, h.changed_by, NULL),
            (CASE WHEN h.new_status = 'Closed' AND h.prev_status = 'Submitted' THEN 'submit_finish' END, h.changed_by,
             CAST(DATEDIFF(MINUTE, h.prev_at, h.changed_at) / 60.0 AS DECIMAL(18,2))),
            (CASE WHEN h.old_status = 'Submitted' AND h.new_status = 'Open' AND h.prev_status = 'Submitted' THEN 'sent_back' END, h.prev_by, NULL),
            (CASE WHEN h.old_status = 'Closed' AND h.new_status = 'Open' AND h.prev_status = 'Closed' THEN 'reopened' END, h.prev_by, NULL)
        ) e (Kpi, [User], Dur)
        WHERE  e.Kpi IS NOT NULL AND h.changed_at >= @fromUtc AND h.changed_at < @toUtc AND " + InScope + @"
          AND  (e.Dur IS NULL OR e.Dur >= 0);

        -- 2. Arrivals registered / completed.
        SELECT CASE WHEN h.old_status IS NULL THEN 'arrivals_created' ELSE 'arrivals_completed' END AS Kpi,
               h.changed_by AS [User], CAST(NULL AS BIGINT) AS QoId, CAST(NULL AS NVARCHAR(40)) AS QoNo,
               a.arrival_id AS ArrivalId, a.arrival_no AS ArrivalNo, a.container_no AS ContainerNo, a.plant AS Plant,
               1 AS Qty, CAST(NULL AS DECIMAL(18,2)) AS Dur, h.changed_at AS At
        FROM   qms_status_history h
        JOIN   qms_arrival a ON a.arrival_id = h.entity_id
        WHERE  h.entity_type = 'Arrival'
          AND  ((h.old_status IS NULL AND h.new_status = 'Draft') OR (h.old_status = 'Draft' AND h.new_status = 'Completed'))
          AND  h.changed_at >= @fromUtc AND h.changed_at < @toUtc AND " + InScope + @";

        -- 3. Samples per order: entered, cartons, deleted, weight readings and slips.
        SELECT s.created_by AS [User], " + Ref + @",
               SUM(CASE WHEN s.is_deleted = 0 THEN 1 ELSE 0 END) AS Samples,
               SUM(CASE WHEN s.is_deleted = 0 THEN CAST(ISNULL(s.sample_size, 0) AS DECIMAL(18,2)) ELSE 0 END) AS Cartons,
               SUM(CASE WHEN s.is_deleted = 1 THEN 1 ELSE 0 END) AS Deleted,
               SUM(ISNULL(w.n, 0)) AS WeightReadings, SUM(ISNULL(w.bad, 0)) AS Slips,
               MAX(s.created_at) AS At
        FROM   qms_sample s
        JOIN   qms_quality_order_material m ON m.qo_material_id = s.qo_material_id
        JOIN   qms_quality_order qo         ON qo.quality_order_id = m.quality_order_id
        JOIN   qms_arrival a                ON a.arrival_id = qo.arrival_id
        LEFT JOIN qms_sap_material_cache mc ON mc.material_no = m.material_no
        CROSS APPLY (SELECT NULLIF(ISNULL(NULLIF(m.net_weight, 0), mc.weight), 0) AS pw) p
        OUTER APPLY (SELECT COUNT(*) AS n, SUM(k.bad) AS bad
                     FROM   qms_sample_reading sr
                     CROSS APPLY (SELECT CASE WHEN p.pw IS NOT NULL
                                               AND (sr.numeric_value > p.pw * @slip OR sr.numeric_value < p.pw / @slip)
                                              THEN 1 ELSE 0 END AS bad) k
                     WHERE  sr.sample_id = s.sample_id AND s.is_deleted = 0
                       AND  sr.reading_type_code IN ('GROSS_WEIGHT', 'NET_WEIGHT') AND sr.numeric_value IS NOT NULL) w
        WHERE  s.created_at >= @fromUtc AND s.created_at < @toUtc AND " + InScope + @"
        GROUP  BY s.created_by, qo.quality_order_id, qo.quality_order_no, a.arrival_id, a.arrival_no, a.container_no, a.plant;

        -- 4. Photos (sample and arrival photos).
        SELECT 'photos' AS Kpi, il.created_by AS [User], qo.quality_order_id AS QoId, qo.quality_order_no AS QoNo,
               a.arrival_id AS ArrivalId, a.arrival_no AS ArrivalNo, a.container_no AS ContainerNo, a.plant AS Plant,
               COUNT(*) AS Qty, CAST(NULL AS DECIMAL(18,2)) AS Dur, MAX(il.created_at) AS At
        FROM   qms_image_link il
        LEFT JOIN qms_sample s ON il.owner_type = 'Sample' AND s.sample_id = il.owner_id
        LEFT JOIN qms_quality_order_material m ON m.qo_material_id = s.qo_material_id
        LEFT JOIN qms_quality_order qo ON qo.quality_order_id = m.quality_order_id
        JOIN   qms_arrival a ON a.arrival_id = COALESCE(qo.arrival_id, CASE WHEN il.owner_type = 'Arrival' THEN il.owner_id END)
        WHERE  il.owner_type IN ('Sample', 'Arrival') AND il.created_at >= @fromUtc AND il.created_at < @toUtc AND " + InScope + @"
        GROUP  BY il.created_by, qo.quality_order_id, qo.quality_order_no, a.arrival_id, a.arrival_no, a.container_no, a.plant;

        -- 5. Audited actions: overrides, corrections (values really changed), claim decisions.
        SELECT k.Kpi, al.changed_by AS [User], " + Ref + @", COUNT(*) AS Qty,
               CAST(NULL AS DECIMAL(18,2)) AS Dur, MAX(al.changed_at) AS At
        FROM   qms_audit_log al
        CROSS APPLY (SELECT CASE
                WHEN al.entity_type = 'QualityOrderMaterial' AND al.action_code = 'Override' THEN 'overrides'
                WHEN al.entity_type IN ('SampleReading', 'SampleDefect') AND al.action_code = 'Updated' THEN 'corrections'
                WHEN al.entity_type = 'Claim' AND al.action_code = 'ClaimRequest' THEN 'claim_requests'
                WHEN al.entity_type = 'Claim' AND al.action_code = 'PassedQC'     THEN 'passed_qc'
                WHEN al.entity_type = 'Claim' AND al.action_code = 'Approved'     THEN 'claims_reviewed'
                WHEN al.entity_type = 'Claim' AND al.action_code = 'Hold'         THEN 'claims_hold' END AS Kpi) k
        LEFT JOIN qms_sample s ON al.entity_type IN ('SampleReading', 'SampleDefect') AND s.sample_id = al.entity_id
        LEFT JOIN qms_quality_order_material m
               ON m.qo_material_id = CASE WHEN al.entity_type = 'QualityOrderMaterial' THEN al.entity_id ELSE s.qo_material_id END
        LEFT JOIN qms_claim cl ON al.entity_type = 'Claim' AND cl.claim_id = al.entity_id
        JOIN   qms_quality_order qo ON qo.quality_order_id = COALESCE(m.quality_order_id, cl.quality_order_id)
        JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
        WHERE  k.Kpi IS NOT NULL AND al.changed_at >= @fromUtc AND al.changed_at < @toUtc AND " + InScope + @"
          -- A re-save counts as a correction only when it changed something: a
          -- different number of readings, or a reading / defect value that is
          -- not in the new set. (The two sides are serialised with different
          -- key names, so values are compared, not strings.)
          AND  (k.Kpi <> 'corrections' OR (TRY_CAST(JSON_VALUE(al.old_values_json, '$.count') AS INT) > 0 AND (
                 TRY_CAST(JSON_VALUE(al.old_values_json, '$.count') AS INT) <> TRY_CAST(JSON_VALUE(al.new_values_json, '$.count') AS INT)
              OR (al.entity_type = 'SampleReading' AND EXISTS (
                    SELECT c, nv, tv FROM OPENJSON(al.old_values_json, '$.readings')
                        WITH (c NVARCHAR(60) '$.reading_type_code', nv DECIMAL(18,4) '$.numeric_value', tv NVARCHAR(400) '$.text_value')
                    EXCEPT
                    SELECT c, nv, tv FROM OPENJSON(al.new_values_json, '$.readings')
                        WITH (c NVARCHAR(60) '$.ReadingTypeCode', nv DECIMAL(18,4) '$.NumericValue', tv NVARCHAR(400) '$.TextValue')))
              OR (al.entity_type = 'SampleDefect' AND EXISTS (
                    SELECT d, v FROM OPENJSON(al.old_values_json, '$.defects')
                        WITH (d INT '$.defect_id', v DECIMAL(18,4) '$.defect_value')
                    EXCEPT
                    SELECT d, v FROM OPENJSON(al.new_values_json, '$.defects')
                        WITH (d INT '$.DefectId', v DECIMAL(18,4) '$.DefectValue'))))))
        GROUP  BY k.Kpi, al.changed_by, qo.quality_order_id, qo.quality_order_no, a.arrival_id, a.arrival_no, a.container_no, a.plant;

        -- 6. Claims decided (time from raised to decided), potential flags, notes.
        SELECT 'claim_decision' AS Kpi, cl.decided_by AS [User], " + Ref + @", 1 AS Qty,
               CAST(DATEDIFF(MINUTE, cl.created_at, cl.decided_at) / 60.0 AS DECIMAL(18,2)) AS Dur, cl.decided_at AS At
        FROM   qms_claim cl
        JOIN   qms_quality_order qo ON qo.quality_order_id = cl.quality_order_id
        JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
        WHERE  cl.decided_at >= @fromUtc AND cl.decided_at < @toUtc AND cl.decided_by IS NOT NULL
          AND  cl.decided_at >= cl.created_at AND " + InScope + @"
        UNION ALL
        SELECT 'potential_flags', qo.potential_claim_by, " + Ref + @", 1, NULL, qo.potential_claim_at
        FROM   qms_quality_order qo
        JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
        WHERE  qo.potential_claim = 1 AND qo.potential_claim_by IS NOT NULL
          AND  qo.potential_claim_at >= @fromUtc AND qo.potential_claim_at < @toUtc AND " + InScope + @"
        UNION ALL
        SELECT 'notes', cn.created_by, " + Ref + @", 1, NULL, cn.created_at
        FROM   qms_claim_note cn
        JOIN   qms_claim cl ON cl.claim_id = cn.claim_id
        JOIN   qms_quality_order qo ON qo.quality_order_id = cl.quality_order_id
        JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
        WHERE  cn.note_kind = 'Comment' AND cn.created_at >= @fromUtc AND cn.created_at < @toUtc AND " + InScope + @"
        UNION ALL
        -- Reports e-mailed; the first send of an order also times finish -> send.
        SELECT 'reports_sent', rs.sent_by, " + Ref + @", 1,
               CASE WHEN rs.send_id = (SELECT MIN(x.send_id) FROM qms_qo_report_send x WHERE x.quality_order_id = rs.quality_order_id)
                         AND qo.closed_at IS NOT NULL AND rs.sent_at >= qo.closed_at
                    THEN CAST(DATEDIFF(MINUTE, qo.closed_at, rs.sent_at) / 60.0 AS DECIMAL(18,2)) END,
               rs.sent_at
        FROM   qms_qo_report_send rs
        JOIN   qms_quality_order qo ON qo.quality_order_id = rs.quality_order_id
        JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
        WHERE  rs.sent_at >= @fromUtc AND rs.sent_at < @toUtc AND " + InScope + @"
        UNION ALL
        -- Finished orders later sent for reinspection, against whoever finished them.
        SELECT 'reinspected', qo.closed_by, " + Ref + @", 1, NULL, qo.superseded_at
        FROM   qms_quality_order qo
        JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
        WHERE  qo.superseded_at >= @fromUtc AND qo.superseded_at < @toUtc AND qo.closed_by IS NOT NULL AND " + InScope + @";

        -- 7. Active days (any saved change, local date).
        SELECT changed_by AS [User], COUNT(DISTINCT CAST(DATEADD(MINUTE, @tz, changed_at) AS DATE)) AS Days
        FROM   qms_audit_log
        WHERE  changed_at >= @fromUtc AND changed_at < @toUtc
        GROUP  BY changed_by;";

    private sealed class SampleAgg
    {
        public string User { get; set; } = "";
        public long QoId { get; set; }
        public string? QoNo { get; set; }
        public long ArrivalId { get; set; }
        public string? ArrivalNo { get; set; }
        public string? ContainerNo { get; set; }
        public string? Plant { get; set; }
        public int Samples { get; set; }
        public decimal Cartons { get; set; }
        public int Deleted { get; set; }
        public int WeightReadings { get; set; }
        public int Slips { get; set; }
        public DateTime At { get; set; }
    }

    /// <summary>The local dates converted to a UTC [from, to) window, clamped
    /// to <see cref="MaxDays"/>.</summary>
    public static (DateTime From, DateTime To, DateTime FromUtc, DateTime ToUtc) Window(DateTime from, DateTime to)
    {
        from = from.Date; to = to.Date;
        if (to < from) (from, to) = (to, from);
        if ((to - from).TotalDays > MaxDays) from = to.AddDays(-MaxDays);
        return (from, to,
                DateTime.SpecifyKind(from, DateTimeKind.Local).ToUniversalTime(),
                DateTime.SpecifyKind(to.AddDays(1), DateTimeKind.Local).ToUniversalTime());
    }

    /// <summary>The account name as the *_by columns hold it: no domain, lower case.</summary>
    public static string Norm(string? u)
    {
        u = (u ?? "").Trim();
        var at = u.IndexOf('@');
        return (at >= 0 ? u[..at] : u).ToLowerInvariant();
    }

    private async Task<(List<KpiEvent> Events, Dictionary<string, int> Days)> LoadAsync(
        DateTime fromUtc, DateTime toUtc, PlantScope scope, CancellationToken ct)
    {
        var key = $"kpi:{fromUtc:O}:{toUtc:O}:{scope.Unrestricted}:{string.Join(",", scope.Plants.OrderBy(p => p))}";
        if (_cache.TryGetValue(key, out (List<KpiEvent>, Dictionary<string, int>) hit)) return hit;
        var loaded = await LoadFreshAsync(fromUtc, toUtc, scope, ct);
        _cache.Set(key, loaded, CacheFor);
        return loaded;
    }

    private async Task<(List<KpiEvent> Events, Dictionary<string, int> Days)> LoadFreshAsync(
        DateTime fromUtc, DateTime toUtc, PlantScope scope, CancellationToken ct)
    {
        using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        using var grid = await c.QueryMultipleAsync(new CommandDefinition(Sql, new
        {
            fromUtc, toUtc, slip = DefectReportService.WeightSlipFactor,
            tz = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes,
            sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants
        }, commandTimeout: 180, cancellationToken: ct));

        var events = new List<KpiEvent>();
        events.AddRange(await grid.ReadAsync<KpiEvent>());   // 1 transitions
        events.AddRange(await grid.ReadAsync<KpiEvent>());   // 2 arrivals
        foreach (var s in await grid.ReadAsync<SampleAgg>())  // 3 samples
        {
            KpiEvent E(string kpi, decimal qty) => new()
            {
                Kpi = kpi, User = s.User, QoId = s.QoId, QoNo = s.QoNo, ArrivalId = s.ArrivalId, ArrivalNo = s.ArrivalNo,
                ContainerNo = s.ContainerNo, Plant = s.Plant, Qty = qty, At = s.At
            };
            if (s.Samples > 0) { events.Add(E("samples", s.Samples)); events.Add(E("cartons", s.Cartons)); }
            if (s.Samples + s.Deleted > 0) events.Add(E("samples_all", s.Samples + s.Deleted));
            if (s.Deleted > 0) events.Add(E("deleted_samples", s.Deleted));
            if (s.WeightReadings > 0) events.Add(E("weight_readings", s.WeightReadings));
            if (s.Slips > 0) events.Add(E("weight_slips", s.Slips));
        }
        events.AddRange(await grid.ReadAsync<KpiEvent>());   // 4 photos
        events.AddRange(await grid.ReadAsync<KpiEvent>());   // 5 audited actions
        foreach (var e in await grid.ReadAsync<KpiEvent>())  // 6 claims, sends, reinspections
        {
            events.Add(e);
            if (e.Kpi == "claim_decision")
                events.Add(new KpiEvent { Kpi = "claim_decision_c", User = e.User, QoId = e.QoId, QoNo = e.QoNo, ArrivalId = e.ArrivalId,
                                          ArrivalNo = e.ArrivalNo, ContainerNo = e.ContainerNo, Plant = e.Plant, Dur = e.Dur, At = e.At });
            if (e.Kpi == "reports_sent" && e.Dur.HasValue)
                events.Add(new KpiEvent { Kpi = "finish_send", User = e.User, QoId = e.QoId, QoNo = e.QoNo, ArrivalId = e.ArrivalId,
                                          ArrivalNo = e.ArrivalNo, ContainerNo = e.ContainerNo, Plant = e.Plant, Dur = e.Dur, At = e.At });
        }
        var days = (await grid.ReadAsync<(string User, int Days)>())
            .GroupBy(x => Norm(x.User)).ToDictionary(g => g.Key, g => g.Sum(x => x.Days));

        foreach (var e in events)
        {
            e.User = Norm(e.User);
            if (e.Kpi != "finish_send" && e.Kpi != "claim_decision_c" && e.Kpi is not ("open_submit" or "submit_finish" or "recv_open" or "claim_decision"))
                e.Dur = null;
        }
        // reports_sent counts sends; its duration lives on finish_send.
        events.RemoveAll(e => e.User.Length == 0 || SystemActors.Contains(e.User));
        return (events, days);
    }

    public async Task<KpiDataset> GetAsync(DateTime from, DateTime to, PlantScope scope, CancellationToken ct = default)
    {
        var w = Window(from, to);
        var (events, days) = await LoadAsync(w.FromUtc, w.ToUtc, scope, ct);

        var values = new Dictionary<string, Dictionary<string, decimal?[]>>();
        foreach (var g in events.GroupBy(e => e.Kpi))
        {
            var byUser = new Dictionary<string, decimal?[]>();
            foreach (var u in g.GroupBy(e => e.User))
            {
                var durs = u.Where(e => e.Dur.HasValue).Select(e => e.Dur!.Value).OrderBy(x => x).ToList();
                byUser[u.Key] = durs.Count > 0
                    ? new decimal?[] { durs.Count, Median(durs), Math.Round(durs.Average(), 2) }
                    : new decimal?[] { u.Sum(e => e.Qty), null, null };
            }
            values[g.Key] = byUser;
        }

        // Active days only for people who did something in scope.
        var people = events.Select(e => e.User).ToHashSet();
        values["active_days"] = days.Where(kv => people.Contains(kv.Key))
                                    .ToDictionary(kv => kv.Key, kv => new decimal?[] { kv.Value, null, null });

        var users = await UsersAsync(people);
        return new KpiDataset
        {
            From = w.From.ToString("yyyy-MM-dd"), To = w.To.ToString("yyyy-MM-dd"),
            Kpis = Kpis.ToList(), Users = users, Values = values,
            PlantNames = _codes.Plants.Select(p => p.Code)
                .Concat(users.SelectMany(u => u.Plants)).Distinct()
                .ToDictionary(p => p, p => _codes.PlantDisplay(p)),
            Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
        };
    }

    public static decimal Median(IReadOnlyList<decimal> sorted) =>
        sorted.Count == 0 ? 0
        : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
        : Math.Round((sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2, 2);

    private async Task<List<KpiUser>> UsersAsync(IReadOnlyCollection<string> people)
    {
        using var c = new SqlConnection(_cs);
        var rows = (await c.QueryAsync<(int UserId, string Username, string? FullName, string? Role)>(
            "SELECT UserId, Username, FullName, Role FROM qms.AppUser")).ToList();
        var plants = (await c.QueryAsync<(int UserId, string Plant)>(
            "SELECT UserId, Plant FROM portal.UserPlant")).ToLookup(x => x.UserId, x => x.Plant);
        var known = rows.GroupBy(r => Norm(r.Username)).ToDictionary(g => g.Key, g => g.First());
        return people.OrderBy(p => p).Select(p => known.TryGetValue(p, out var r)
                ? new KpiUser { User = p, Name = string.IsNullOrWhiteSpace(r.FullName) ? p : r.FullName!, Role = RoleLabel(r.Role),
                                Plants = plants[r.UserId].OrderBy(x => x).ToList() }
                : new KpiUser { User = p, Name = p, Role = "(no QMS role)" })
            .ToList();
    }

    private static string RoleLabel(string? role) => role switch
    {
        "SiteAdmin" or "QcAdmin" => "Admin",
        null or "" => "",
        _ when role.StartsWith("Qc", StringComparison.Ordinal) => role[2..],
        _ => role
    };

    public async Task<IReadOnlyList<KpiEvent>> DrillAsync(string kpi, string user, DateTime from, DateTime to, PlantScope scope,
                                                          CancellationToken ct = default)
    {
        var def = Kpis.FirstOrDefault(k => k.Key == kpi);
        var key = def?.Num ?? kpi;
        if (def == null && !Hidden.Contains(kpi)) return Array.Empty<KpiEvent>();
        var w = Window(from, to);
        var (events, _) = await LoadAsync(w.FromUtc, w.ToUtc, scope, ct);
        var who = Norm(user);
        return events.Where(e => e.Kpi == key && e.User == who).OrderByDescending(e => e.At).Take(3000).ToList();
    }
}
