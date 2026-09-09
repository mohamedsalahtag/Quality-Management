using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Aggregation queries that feed the home dashboard. Designed to land
/// in a small number of round trips so the dashboard renders quickly
/// even when the QMS database has tens of thousands of arrivals / QOs.
/// AlertConfig thresholds come from site settings so the "stale" /
/// "aged" counters reflect whatever the admin configured.
///
/// Every query is narrowed two ways:
///
///   * PLANT — the user's entitlement (<see cref="PlantScope"/>), optionally
///     narrowed further to the one plant the filter names. Plant lives on
///     <c>qms_arrival.plant</c>; quality orders reach it through their arrival.
///   * PERIOD — the selected date range, applied to everything with a natural
///     event date (received, committed, completed, defects, both trends).
///     Backlog figures (draft arrivals, open QOs, stale, aged, containers with
///     no arrival) are deliberately left as "as of now": a backlog filtered to
///     last Tuesday is not a backlog, and the tiles say so.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly string _cs;
    private readonly ISettingsService _settings;

    public DashboardService(IConfiguration config, ISettingsService settings)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _settings = settings;
    }

    public async Task<DashboardVm> GetSummaryAsync(DashboardFilter filter, PlantScope scope,
        CancellationToken ct = default)
    {
        filter ??= new DashboardFilter();
        scope  ??= PlantScope.All;

        // A filter may narrow the user's entitlement, never widen it. Silently
        // dropping an out-of-scope plant (rather than erroring) means a
        // bookmarked link from someone with wider access degrades to the
        // reader's own plants instead of a permission wall.
        if (!string.IsNullOrWhiteSpace(filter.Plant) && !scope.Allows(filter.Plant))
            filter.Plant = null;

        var thresholds = await _settings.GetAlertConfigAsync();
        var agg = await LoadAggregatesAsync(filter, scope, thresholds);

        return new DashboardVm
        {
            Filter             = filter,
            PlantOptions       = agg.PlantOptions,
            Commitment         = agg.Commitment,
            CommitmentTrend    = agg.CommitmentTrend,
            Counts             = agg.Counts,
            ArrivalsByStatus   = agg.ArrivalsByStatus,
            QoByStatus         = agg.QoByStatus,
            ArrivalsTrend      = agg.ArrivalsTrend,
            AvgDefectPctLast30 = agg.AvgDefectPctLast30,
            Thresholds         = thresholds,
            OpenArrivals       = agg.OpenArrivals,
            OpenQos            = agg.OpenQos,
            ThroughputTrend    = agg.ThroughputTrend,
            OpenQoAgeBuckets   = agg.OpenQoAgeBuckets,
            DefectByGroup      = agg.DefectByGroup,
            DefectRate         = agg.DefectRate,
            SyncFailures       = await GetRecentSyncFailuresAsync()
        };
    }

    // Surfaces SAP-sync failures so an admin sees them on the dashboard instead
    // of only in the Event Log / SAP settings tab. Returns the endpoints whose
    // most recent sync run failed.
    private async Task<List<SyncFailure>> GetRecentSyncFailuresAsync()
    {
        try
        {
            using var c = new SqlConnection(_cs);
            var rows = await c.QueryAsync<SyncFailure>(@"
                SELECT l.endpoint_key AS EndpointKey,
                       l.completed_at AS CompletedAt,
                       l.message      AS Message
                FROM   qms_sap_sync_log l
                JOIN  (SELECT endpoint_key, MAX(started_at) AS mx
                       FROM qms_sap_sync_log GROUP BY endpoint_key) t
                       ON t.endpoint_key = l.endpoint_key AND t.mx = l.started_at
                WHERE  l.success = 0");
            return rows.ToList();
        }
        catch
        {
            // Best-effort: never let a sync-log read break the dashboard.
            return new List<SyncFailure>();
        }
    }

    private async Task<Aggregates> LoadAggregatesAsync(
        DashboardFilter filter, PlantScope scope, AlertConfig thresholds)
    {
        var (fromLocal, toLocal) = filter.Resolve();
        // Local dates -> a half-open UTC window. Half-open (>= from, < to+1day)
        // rather than BETWEEN: created_at carries a time, so an inclusive upper
        // bound of midnight would drop everything recorded during the last day.
        var fromUtc  = DateTime.SpecifyKind(fromLocal, DateTimeKind.Local).ToUniversalTime();
        var toUtcEx  = DateTime.SpecifyKind(toLocal.AddDays(1), DateTimeKind.Local).ToUniversalTime();

        // Bucket width follows the period: hours for a single day, days up to a
        // month, weeks beyond. A one-day period bucketed BY DAY is a single
        // point, which is what made the trend charts look broken on the default
        // view. Interpolated into the SQL rather than parameterised because
        // DATEPART names are not parameterisable — the value comes from a
        // closed set in DashboardFilter, never from user input.
        var bucket = filter.BucketPart;

        using var c = new SqlConnection(_cs);
        await c.OpenAsync();

        // The plant predicate, repeated wherever an arrival is in play. @plant
        // narrows to one; the scope pair limits to what the user holds.
        const string plantWhere =
            "(@plant IS NULL OR a.plant = @plant) AND (@sUnrestricted = 1 OR a.plant IN @sPlants)";

        // Single QueryMultiple = one round trip with many result sets.
        // Keeps SAP-cache + arrivals + QOs + defects under one connection
        // so the dashboard paints fast even on a busy DB.
        var sql = $@"
            -- 1) Arrivals grouped by status (backlog: not period-scoped)
            SELECT a.status_code, COUNT(*) AS Cnt
            FROM   qms_arrival a
            WHERE  {plantWhere}
            GROUP  BY a.status_code;

            -- 2) QOs grouped by status (backlog: not period-scoped)
            SELECT qo.status_code, COUNT(*) AS Cnt
            FROM   qms_quality_order qo
            JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
            WHERE  {plantWhere}
            GROUP  BY qo.status_code;

            -- 3) Arrivals trend over the period (Created + Completed)
            SELECT  DATEADD({bucket}, DATEDIFF({bucket}, 0, a.created_at), 0)        AS WeekStart,
                    SUM(1)                                                          AS Created,
                    SUM(CASE WHEN a.completed_at IS NOT NULL THEN 1 ELSE 0 END)      AS Completed
            FROM    qms_arrival a
            WHERE   a.created_at >= @fromUtc AND a.created_at < @toUtcEx
              AND   {plantWhere}
            GROUP   BY DATEADD({bucket}, DATEDIFF({bucket}, 0, a.created_at), 0)
            ORDER   BY 1;

            -- 4) Composite counts. Stale / aged / pending are ""as of now"" by
            --    design; only CompletedInPeriod follows the selected range.
            SELECT
                (SELECT COUNT(*) FROM qms_arrival a
                  WHERE a.status_code = 'Draft'
                    AND a.created_at < DATEADD(day, -@stale, SYSUTCDATETIME())
                    AND {plantWhere})                                                AS StaleArrivals,
                (SELECT COUNT(*) FROM qms_quality_order qo
                  JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                  WHERE qo.status_code IN ('Initial','Open','Reopened')
                    AND COALESCE(qo.opened_at, qo.created_at) < DATEADD(day, -@openDays, SYSUTCDATETIME())
                    AND {plantWhere})                                                AS AgedOpenQos,
                (SELECT COUNT(*) FROM qms_arrival a
                  WHERE a.status_code = 'Completed'
                    AND a.completed_at >= @fromUtc AND a.completed_at < @toUtcEx
                    AND {plantWhere})                                                AS CompletedInPeriod,
                -- Pending containers live in the SAP cache, which carries its own
                -- effective plant (a manager override wins over SAP's) and its own
                -- archive flag -- an archived container is not pending.
                (SELECT COUNT(*) FROM (
                    SELECT DISTINCT cc.container_no, cc.bol_no, cc.ebeln
                    FROM   qms_sap_container_cache cc
                    WHERE  cc.has_arrival = 0
                      AND  cc.archived_at IS NULL
                      AND (@plant IS NULL OR COALESCE(cc.override_plant, cc.plant) = @plant)
                      AND (@sUnrestricted = 1 OR COALESCE(cc.override_plant, cc.plant) IN @sPlants)
                ) AS p)                                                              AS PendingContainers;

            -- 5) Defect rate over the period, WITH ITS PROVENANCE.
            -- Computed as total defective units / total inspected units, NOT as
            -- AVG(defect_percentage). The old average-of-percentages was distorted:
            -- it divided by the catalog size (adding a defect type lowered the KPI
            -- with no quality change) and over-weighted heavily-sampled QOs. This
            -- weights by sample size and is independent of catalog size.
            --
            -- The counts travel with it because a bare gauge answers ""how bad""
            -- and nothing else: how many orders, how many samples, how many
            -- units. A rate over four samples and a rate over four hundred are
            -- not the same claim, and the reader could not tell them apart.
            SELECT CASE WHEN SUM(x.inspected) > 0
                        THEN SUM(x.defective) * 100.0 / SUM(x.inspected)
                        ELSE NULL END          AS Pct,
                   COUNT(DISTINCT x.qo_id)     AS Orders,
                   COUNT(*)                    AS Samples,
                   ISNULL(SUM(x.defective), 0) AS DefectiveUnits,
                   ISNULL(SUM(x.inspected), 0) AS InspectedUnits
            FROM (
                SELECT s.sample_id,
                       MAX(qo.quality_order_id) AS qo_id,
                       MAX(CAST(s.sample_size AS DECIMAL(18,4))) AS inspected,
                       SUM(CAST(sd.defect_value AS DECIMAL(18,4))) AS defective
                FROM   qms_sample        s
                JOIN   qms_quality_order qo ON qo.quality_order_id = s.quality_order_id
                JOIN   qms_arrival       a  ON a.arrival_id = qo.arrival_id
                LEFT JOIN qms_sample_defect sd ON sd.sample_id = s.sample_id
                WHERE  qo.status_code = 'Closed'
                  AND  qo.closed_at >= @fromUtc AND qo.closed_at < @toUtcEx
                  AND  s.is_deleted = 0
                  AND  s.sample_size > 0
                  AND  {plantWhere}
                GROUP BY s.sample_id
            ) x;

            -- 5b) WHICH defects the rate is made of. Which defect, which
            -- material -- that is the first question the gauge provokes, so the
            -- five contributing the most units answer it in place.
            SELECT TOP 5
                   dc.defect_name                              AS DefectName,
                   MAX(dc.defect_category)                     AS DefectCategory,
                   MAX(dc.material_group)                      AS MaterialGroup,
                   SUM(CAST(sd.defect_value AS DECIMAL(18,4))) AS Units
            FROM   qms_sample_defect  sd
            JOIN   qms_defect_catalog dc ON dc.defect_id = sd.defect_id
            JOIN   qms_sample         s  ON s.sample_id  = sd.sample_id
            JOIN   qms_quality_order  qo ON qo.quality_order_id = s.quality_order_id
            JOIN   qms_arrival        a  ON a.arrival_id = qo.arrival_id
            WHERE  qo.status_code = 'Closed'
              AND  qo.closed_at >= @fromUtc AND qo.closed_at < @toUtcEx
              AND  s.is_deleted = 0
              AND  sd.defect_value > 0
              AND  {plantWhere}
            GROUP  BY dc.defect_name
            ORDER  BY SUM(CAST(sd.defect_value AS DECIMAL(18,4))) DESC;

            -- 6) Open arrivals (Draft) top 10 newest
            SELECT TOP 10
                a.arrival_id   AS ArrivalId,
                a.arrival_no   AS ArrivalNo,
                a.container_no AS ContainerNo,
                a.bol_no       AS BolNo,
                a.ebeln        AS Ebeln,
                a.vendor_name  AS VendorName,
                a.plant        AS Plant,
                a.status_code  AS StatusCode,
                a.created_at   AS CreatedAt,
                a.created_by   AS CreatedBy
            FROM   qms_arrival a
            WHERE  a.status_code = 'Draft' AND {plantWhere}
            ORDER  BY a.created_at DESC;

            -- 7) Open QOs (Initial/Open/Reopened) top 10 oldest-first so the team
            --    tackles the most-stuck work first.
            SELECT TOP 10
                qo.quality_order_id AS QualityOrderId,
                qo.quality_order_no AS QualityOrderNo,
                qo.arrival_id       AS ArrivalId,
                qo.status_code      AS StatusCode,
                qo.opened_at        AS OpenedAt,
                qo.created_at       AS CreatedAt,
                qo.created_by       AS CreatedBy,
                a.container_no      AS ContainerNo,
                a.bol_no            AS BolNo,
                a.ebeln             AS Ebeln,
                a.vendor_name       AS VendorName,
                a.arrival_no        AS ArrivalNo,
                a.plant             AS Plant
            FROM   qms_quality_order qo
            JOIN   qms_arrival       a ON a.arrival_id = qo.arrival_id
            WHERE  qo.status_code IN ('Initial','Open','Reopened') AND {plantWhere}
            ORDER  BY COALESCE(qo.opened_at, qo.created_at) ASC;

            -- 8) Throughput vs QC pace over the period: arrivals received vs QOs closed
            SELECT b.Bucket AS WeekStart,
                   (SELECT COUNT(*) FROM qms_arrival a
                     WHERE DATEADD({bucket}, DATEDIFF({bucket}, 0, a.created_at), 0) = b.Bucket
                       AND {plantWhere})                                        AS ArrivalsRecv,
                   (SELECT COUNT(*) FROM qms_quality_order qo
                     JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                     WHERE qo.status_code = 'Closed'
                       AND DATEADD({bucket}, DATEDIFF({bucket}, 0, qo.closed_at), 0) = b.Bucket
                       AND {plantWhere})                                        AS QosClosed
            FROM (
                -- The buckets actually spanned by the period, taken from the two
                -- event tables so an empty bucket still appears (as a zero) when
                -- its neighbours have data.
                SELECT DISTINCT DATEADD({bucket}, DATEDIFF({bucket}, 0, d.dt), 0) AS Bucket
                FROM (
                    SELECT a.created_at AS dt FROM qms_arrival a
                     WHERE a.created_at >= @fromUtc AND a.created_at < @toUtcEx AND {plantWhere}
                    UNION ALL
                    SELECT qo.closed_at FROM qms_quality_order qo
                     JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                     WHERE qo.closed_at >= @fromUtc AND qo.closed_at < @toUtcEx AND {plantWhere}
                ) d
            ) b
            ORDER BY b.Bucket;

            -- 9) Open-QO age, ONE BUCKET PER DAY (0..13, then 14+).
            -- The old 0-3 / 3-7 / 7-14 ranges hid the thing the chart is for:
            -- whether a particular day's work is piling up. A bar per day shows
            -- the shape; anything a fortnight old is a single tail bucket
            -- because at that point the exact day has stopped mattering.
            SELECT CASE WHEN ageDays >= 14 THEN 14
                        WHEN ageDays < 0   THEN 0
                        ELSE ageDays END AS AgeDays,
                   COUNT(*)              AS Cnt
            FROM (
                SELECT DATEDIFF(day, COALESCE(qo.opened_at, qo.created_at), SYSUTCDATETIME()) AS ageDays
                FROM   qms_quality_order qo
                JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
                WHERE  qo.status_code IN ('Initial','Open','Reopened') AND {plantWhere}
            ) src
            GROUP  BY CASE WHEN ageDays >= 14 THEN 14 WHEN ageDays < 0 THEN 0 ELSE ageDays END
            ORDER  BY 1;

            -- 10) Defect % by material group (top 8) over the period.
            -- Weighted defective/inspected, like the headline gauge, NOT
            -- AVG(defect_percentage) -- that averaged per-defect-row
            -- percentages, so a group with many defect types scored worse for
            -- no quality reason. The units and sample counts ride along because
            -- a percentage with nothing behind it cannot be acted on: 12% of
            -- three samples and 12% of three hundred are different facts.
            SELECT TOP 8
                   g.MaterialGroup, g.MaterialGroupDesc,
                   CASE WHEN g.InspectedUnits > 0
                        THEN g.DefectiveUnits * 100.0 / g.InspectedUnits
                        ELSE 0 END AS AvgDefectPct,
                   g.Orders, g.Samples, g.DefectiveUnits, g.InspectedUnits,
                   -- The single worst defect in the group, so the bar says what
                   -- to go and look at rather than only how tall it is.
                   (SELECT TOP 1 dc2.defect_name
                    FROM   qms_sample_defect sd2
                    JOIN   qms_defect_catalog dc2 ON dc2.defect_id = sd2.defect_id
                    JOIN   qms_sample s2  ON s2.sample_id = sd2.sample_id
                    JOIN   qms_quality_order qo2 ON qo2.quality_order_id = s2.quality_order_id
                    JOIN   qms_arrival a2 ON a2.arrival_id = qo2.arrival_id
                    JOIN   qms_quality_order_material qm2 ON qm2.qo_material_id = s2.qo_material_id
                    WHERE  qo2.status_code = 'Closed'
                      AND  qo2.closed_at >= @fromUtc AND qo2.closed_at < @toUtcEx
                      AND  s2.is_deleted = 0 AND sd2.defect_value > 0
                      AND  qm2.material_group = g.MaterialGroup
                      AND  (@plant IS NULL OR a2.plant = @plant)
                      AND  (@sUnrestricted = 1 OR a2.plant IN @sPlants)
                    GROUP  BY dc2.defect_name
                    ORDER  BY SUM(CAST(sd2.defect_value AS DECIMAL(18,4))) DESC) AS TopDefect,
                   0 AS TopDefectUnits
            FROM (
                -- Aggregated in TWO steps on purpose. The LEFT JOIN to
                -- sample_defect repeats a sample once per defect row, so summing
                -- sample_size across the joined set would count the same carton
                -- several times and understate every percentage. Collapse to one
                -- row per (group, sample) first, then add those up.
                SELECT per.MaterialGroup,
                       MAX(per.MaterialGroupDesc)  AS MaterialGroupDesc,
                       COUNT(DISTINCT per.qo_id)   AS Orders,
                       COUNT(*)                    AS Samples,
                       SUM(per.defective)          AS DefectiveUnits,
                       SUM(per.inspected)          AS InspectedUnits
                FROM (
                    SELECT qom.material_group                   AS MaterialGroup,
                           MAX(qom.material_group_desc)         AS MaterialGroupDesc,
                           MAX(qo.quality_order_id)             AS qo_id,
                           s.sample_id,
                           MAX(CAST(s.sample_size AS DECIMAL(18,4))) AS inspected,
                           ISNULL(SUM(CAST(sd.defect_value AS DECIMAL(18,4))), 0) AS defective
                    FROM   qms_sample                  s
                    JOIN   qms_quality_order           qo ON qo.quality_order_id = s.quality_order_id
                    JOIN   qms_arrival                 a  ON a.arrival_id = qo.arrival_id
                    JOIN   qms_quality_order_material  qom ON qom.qo_material_id = s.qo_material_id
                    LEFT JOIN qms_sample_defect        sd ON sd.sample_id = s.sample_id
                    WHERE  qo.status_code = 'Closed'
                      AND  qo.closed_at >= @fromUtc AND qo.closed_at < @toUtcEx
                      AND  s.is_deleted = 0 AND s.sample_size > 0
                      AND  qom.material_group IS NOT NULL
                      AND  {plantWhere}
                    GROUP  BY qom.material_group, s.sample_id
                ) per
                GROUP  BY per.MaterialGroup
            ) g
            ORDER  BY AvgDefectPct DESC;

            -- 11) Received vs committed, per plant, over the period.
            --
            --     Received  = arrivals created in the period (containers taken in).
            --     Committed = HOW MANY OF THOSE now have a quality order.
            --     QosCreated= quality orders raised in the period from ANY
            --                 arrival date -- the team's throughput.
            --
            --     Committed used to be QosCreated, which compared two different
            --     sets of containers: on 2026-09-03 Dammam took in 3 and raised
            --     13 orders, 10 of them against the previous day's arrivals, and
            --     the portlet reported ""3 received, 13 committed, 433% covered"".
            --     Coverage is only meaningful when both halves describe the same
            --     cohort, so Committed is now a property of the received set and
            --     throughput has a column of its own.
            --
            --     FULL JOIN, not an inner one: a plant that raised orders today
            --     but took nothing in must still appear, or the portlet would
            --     hide the very catching-up it exists to show.
            SELECT COALESCE(r.plant, q.plant) AS Plant,
                   ISNULL(r.Received, 0)      AS Received,
                   ISNULL(r.Committed, 0)     AS Committed,
                   ISNULL(q.Cnt, 0)           AS QosCreated
            FROM (
                SELECT x.plant,
                       COUNT(*)      AS Received,
                       SUM(x.hasQo)  AS Committed
                FROM (
                    SELECT a.plant,
                           CASE WHEN EXISTS (SELECT 1 FROM qms_quality_order qq
                                             WHERE qq.arrival_id = a.arrival_id)
                                THEN 1 ELSE 0 END AS hasQo
                    FROM   qms_arrival a
                    WHERE  a.created_at >= @fromUtc AND a.created_at < @toUtcEx
                      AND  {plantWhere}
                ) x
                GROUP BY x.plant
            ) r
            FULL OUTER JOIN (
                SELECT a.plant, COUNT(*) AS Cnt
                FROM   qms_quality_order qo
                JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
                WHERE  qo.created_at >= @fromUtc AND qo.created_at < @toUtcEx AND {plantWhere}
                GROUP  BY a.plant
            ) q ON q.plant = r.plant
            ORDER BY Plant;

            -- 12) The same comparison bucketed over time, for the trend chart.
            SELECT b.Bucket AS Bucket,
                   (SELECT COUNT(*) FROM qms_arrival a
                     WHERE DATEADD({bucket}, DATEDIFF({bucket}, 0, a.created_at), 0) = b.Bucket
                       AND a.created_at >= @fromUtc AND a.created_at < @toUtcEx
                       AND {plantWhere})                                        AS Received,
                   -- Of the containers received IN THIS BUCKET, how many have an
                   -- order. Same cohort as Received, so the two bars are
                   -- comparable at every point on the axis.
                   (SELECT COUNT(*) FROM qms_arrival a
                     WHERE DATEADD({bucket}, DATEDIFF({bucket}, 0, a.created_at), 0) = b.Bucket
                       AND a.created_at >= @fromUtc AND a.created_at < @toUtcEx
                       AND EXISTS (SELECT 1 FROM qms_quality_order qq
                                   WHERE qq.arrival_id = a.arrival_id)
                       AND {plantWhere})                                        AS Committed,
                   (SELECT COUNT(*) FROM qms_quality_order qo
                     JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                     WHERE DATEADD({bucket}, DATEDIFF({bucket}, 0, qo.created_at), 0) = b.Bucket
                       AND qo.created_at >= @fromUtc AND qo.created_at < @toUtcEx
                       AND {plantWhere})                                        AS QosCreated
            FROM (
                SELECT DISTINCT DATEADD({bucket}, DATEDIFF({bucket}, 0, d.dt), 0) AS Bucket
                FROM (
                    SELECT a.created_at AS dt FROM qms_arrival a
                     WHERE a.created_at >= @fromUtc AND a.created_at < @toUtcEx AND {plantWhere}
                    UNION ALL
                    SELECT qo.created_at FROM qms_quality_order qo
                     JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
                     WHERE qo.created_at >= @fromUtc AND qo.created_at < @toUtcEx AND {plantWhere}
                ) d
            ) b
            ORDER BY b.Bucket;

            -- 13) Plants the user may choose between. Drawn from arrivals rather
            --     than a code table so the picker only ever offers a plant that
            --     has something to show.
            SELECT DISTINCT a.plant
            FROM   qms_arrival a
            WHERE  a.plant IS NOT NULL AND a.plant <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.plant;";

        using var grid = await c.QueryMultipleAsync(sql, new
        {
            stale         = thresholds.StaleArrivalDays,
            openDays      = thresholds.OpenQoDays,
            plant         = string.IsNullOrWhiteSpace(filter.Plant) ? null : filter.Plant!.Trim(),
            fromUtc,
            toUtcEx,
            sUnrestricted = scope.Unrestricted,
            sPlants       = scope.QueryPlants
        });

        var arrivalsByStatus = (await grid.ReadAsync<(string status_code, int Cnt)>())
            .ToDictionary(r => r.status_code, r => r.Cnt);
        var qoByStatus = (await grid.ReadAsync<(string status_code, int Cnt)>())
            .ToDictionary(r => r.status_code, r => r.Cnt);
        var trend       = (await grid.ReadAsync<TrendPoint>()).ToList();
        var counts      = await grid.ReadSingleAsync<(int StaleArrivals, int AgedOpenQos, int CompletedInPeriod, int PendingContainers)>();
        var rate        = await grid.ReadSingleAsync<(decimal? Pct, int Orders, int Samples, decimal DefectiveUnits, decimal InspectedUnits)>();
        var topDefects  = (await grid.ReadAsync<TopDefectRow>()).ToList();
        var openArr     = (await grid.ReadAsync<Arrival>()).ToList();
        var openQos     = (await grid.ReadAsync<QualityOrder>()).ToList();
        var throughput  = (await grid.ReadAsync<ThroughputPoint>()).ToList();
        // One row per age in days; spread into a fixed 0..14+ array so a day
        // with no open orders still draws as a gap rather than shifting the axis.
        var ageRows     = (await grid.ReadAsync<(int AgeDays, int Cnt)>()).ToList();
        var defectGroup = (await grid.ReadAsync<DefectGroupPoint>()).ToList();
        var commitment  = (await grid.ReadAsync<PlantCommitmentRow>()).ToList();
        var commitTrend = (await grid.ReadAsync<CommitmentPoint>()).ToList();
        var plants      = (await grid.ReadAsync<string>()).ToList();

        var ageBuckets = new int[15];
        foreach (var (age, cnt) in ageRows)
            if (age >= 0 && age < ageBuckets.Length) ageBuckets[age] = cnt;

        // Each defect's share of everything inspected, so the rows add up
        // towards the headline rate instead of being unanchored quantities.
        foreach (var t in topDefects)
            t.PctOfInspected = rate.InspectedUnits > 0
                ? Math.Round(t.Units * 100m / rate.InspectedUnits, 2)
                : 0m;

        var draft = arrivalsByStatus.TryGetValue(ArrivalStatus.Draft, out var d) ? d : 0;
        var qoOpen = (qoByStatus.TryGetValue("Initial",  out var i) ? i : 0)
                   + (qoByStatus.TryGetValue("Open",     out var o) ? o : 0)
                   + (qoByStatus.TryGetValue("Reopened", out var r) ? r : 0);

        return new Aggregates
        {
            ArrivalsByStatus = arrivalsByStatus,
            QoByStatus       = qoByStatus,
            ArrivalsTrend    = trend,
            AvgDefectPctLast30 = rate.Pct,
            DefectRate = new DefectRateContext
            {
                Orders         = rate.Orders,
                Samples        = rate.Samples,
                DefectiveUnits = rate.DefectiveUnits,
                InspectedUnits = rate.InspectedUnits,
                TopDefects     = topDefects
            },
            Counts = new DashboardCounts
            {
                ArrivalsDraft            = draft,
                ArrivalsCompletedInPeriod= counts.CompletedInPeriod,
                QoOpen                   = qoOpen,
                StaleArrivals            = counts.StaleArrivals,
                AgedOpenQos              = counts.AgedOpenQos,
                PendingContainers        = counts.PendingContainers
            },
            OpenArrivals    = openArr,
            OpenQos         = openQos,
            ThroughputTrend = throughput,
            OpenQoAgeBuckets = ageBuckets,
            DefectByGroup    = defectGroup,
            Commitment       = commitment,
            CommitmentTrend  = commitTrend,
            PlantOptions     = plants
        };
    }

    private sealed class Aggregates
    {
        public DashboardCounts Counts                   { get; set; } = new();
        public Dictionary<string,int> ArrivalsByStatus  { get; set; } = new();
        public Dictionary<string,int> QoByStatus        { get; set; } = new();
        public List<TrendPoint> ArrivalsTrend           { get; set; } = new();
        public decimal? AvgDefectPctLast30              { get; set; }
        public IReadOnlyList<Arrival>      OpenArrivals { get; set; } = Array.Empty<Arrival>();
        public IReadOnlyList<QualityOrder> OpenQos      { get; set; } = Array.Empty<QualityOrder>();
        public List<ThroughputPoint>       ThroughputTrend  { get; set; } = new();
        public int[]                       OpenQoAgeBuckets { get; set; } = new int[15];
        public DefectRateContext           DefectRate       { get; set; } = new();
        public List<DefectGroupPoint>      DefectByGroup    { get; set; } = new();
        public List<PlantCommitmentRow>    Commitment       { get; set; } = new();
        public List<CommitmentPoint>       CommitmentTrend  { get; set; } = new();
        public IReadOnlyList<string>       PlantOptions     { get; set; } = Array.Empty<string>();
    }
}
