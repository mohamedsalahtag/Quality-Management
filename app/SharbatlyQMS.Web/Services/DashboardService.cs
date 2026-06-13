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

    public async Task<DashboardVm> GetSummaryAsync(CancellationToken ct = default)
    {
        var thresholds = await _settings.GetAlertConfigAsync();
        var agg = await LoadAggregatesAsync(thresholds);

        return new DashboardVm
        {
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
            DefectByGroup      = agg.DefectByGroup
        };
    }

    private async Task<Aggregates> LoadAggregatesAsync(AlertConfig thresholds)
    {
        using var c = new SqlConnection(_cs);
        await c.OpenAsync();

        // Single QueryMultiple = one round trip with many result sets.
        // Keeps SAP-cache + arrivals + QOs + defects under one connection
        // so the dashboard paints fast even on a busy DB.
        const string sql = @"
            -- 1) Arrivals grouped by status
            SELECT status_code, COUNT(*) AS Cnt
            FROM   qms_arrival
            GROUP  BY status_code;

            -- 2) QOs grouped by status
            SELECT status_code, COUNT(*) AS Cnt
            FROM   qms_quality_order
            GROUP  BY status_code;

            -- 3) Original arrivals weekly trend (Created + Completed)
            SELECT  DATEADD(week, DATEDIFF(week, 0, created_at), 0) AS WeekStart,
                    SUM(1)                                                                AS Created,
                    SUM(CASE WHEN completed_at IS NOT NULL THEN 1 ELSE 0 END)             AS Completed
            FROM    qms_arrival
            WHERE   created_at >= DATEADD(week, -12, SYSUTCDATETIME())
            GROUP   BY DATEADD(week, DATEDIFF(week, 0, created_at), 0)
            ORDER   BY 1;

            -- 4) Composite counts (stale, aged, completed-last-30, pending containers)
            SELECT
                (SELECT COUNT(*) FROM qms_arrival
                  WHERE status_code = 'Draft'
                    AND created_at < DATEADD(day, -@stale, SYSUTCDATETIME()))                       AS StaleArrivals,
                (SELECT COUNT(*) FROM qms_quality_order
                  WHERE status_code IN ('Initial','Open','Reopened')
                    AND COALESCE(opened_at, created_at) < DATEADD(day, -@openDays, SYSUTCDATETIME())) AS AgedOpenQos,
                (SELECT COUNT(*) FROM qms_arrival
                  WHERE status_code = 'Completed'
                    AND completed_at >= DATEADD(day, -30, SYSUTCDATETIME()))                        AS CompletedLast30,
                (SELECT COUNT(*) FROM (
                    SELECT DISTINCT container_no, bol_no, ebeln
                    FROM   qms_sap_container_cache
                    WHERE  has_arrival = 0
                ) AS p)                                                                              AS PendingContainers;

            -- 5) Avg defect % (closed QOs, 30d)
            SELECT AVG(CAST(sd.defect_percentage AS DECIMAL(9,4)))
            FROM   qms_sample_defect sd
            JOIN   qms_sample        s  ON s.sample_id        = sd.sample_id
            JOIN   qms_quality_order qo ON qo.quality_order_id = s.quality_order_id
            WHERE  qo.status_code = 'Closed'
              AND  qo.closed_at >= DATEADD(day, -30, SYSUTCDATETIME());

            -- 6) Open arrivals (Draft) top 10 newest
            SELECT TOP 10
                a.arrival_id   AS ArrivalId,
                a.arrival_no   AS ArrivalNo,
                a.container_no AS ContainerNo,
                a.bol_no       AS BolNo,
                a.ebeln        AS Ebeln,
                a.vendor_name  AS VendorName,
                a.status_code  AS StatusCode,
                a.created_at   AS CreatedAt,
                a.created_by   AS CreatedBy
            FROM   qms_arrival a
            WHERE  a.status_code = 'Draft'
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
                a.arrival_no        AS ArrivalNo
            FROM   qms_quality_order qo
            JOIN   qms_arrival       a ON a.arrival_id = qo.arrival_id
            WHERE  qo.status_code IN ('Initial','Open','Reopened')
            ORDER  BY COALESCE(qo.opened_at, qo.created_at) ASC;

            -- 8) Throughput vs QC pace (last 12 weeks): arrivals received vs QOs closed
            ;WITH wk AS (
                SELECT TOP 12
                    DATEADD(week, DATEDIFF(week, 0, SYSUTCDATETIME()) - n, 0) AS WeekStart
                FROM (
                    SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL
                    SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL
                    SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11
                ) x
            )
            SELECT
                wk.WeekStart                                            AS WeekStart,
                (SELECT COUNT(*) FROM qms_arrival a
                  WHERE DATEADD(week, DATEDIFF(week, 0, a.created_at), 0) = wk.WeekStart) AS ArrivalsRecv,
                (SELECT COUNT(*) FROM qms_quality_order qo
                  WHERE qo.status_code = 'Closed'
                    AND DATEADD(week, DATEDIFF(week, 0, qo.closed_at), 0) = wk.WeekStart) AS QosClosed
            FROM wk
            ORDER BY wk.WeekStart;

            -- 9) Open-QO age distribution (buckets 0-3 / 3-7 / 7-14 / 14+)
            SELECT
                SUM(CASE WHEN ageDays <  3                   THEN 1 ELSE 0 END) AS B0_3,
                SUM(CASE WHEN ageDays >= 3 AND ageDays <  7  THEN 1 ELSE 0 END) AS B3_7,
                SUM(CASE WHEN ageDays >= 7 AND ageDays < 14  THEN 1 ELSE 0 END) AS B7_14,
                SUM(CASE WHEN ageDays >= 14                  THEN 1 ELSE 0 END) AS B14p
            FROM (
                SELECT DATEDIFF(day, COALESCE(opened_at, created_at), SYSUTCDATETIME()) AS ageDays
                FROM   qms_quality_order
                WHERE  status_code IN ('Initial','Open','Reopened')
            ) src;

            -- 10) Defect % by material group (top 8, last 30 days)
            SELECT TOP 8
                qom.material_group        AS MaterialGroup,
                MAX(qom.material_group_desc) AS MaterialGroupDesc,
                AVG(CAST(sd.defect_percentage AS DECIMAL(9,4))) AS AvgDefectPct
            FROM   qms_sample_defect           sd
            JOIN   qms_sample                  s  ON s.sample_id  = sd.sample_id
            JOIN   qms_quality_order           qo ON qo.quality_order_id = s.quality_order_id
            JOIN   qms_quality_order_material  qom ON qom.qo_material_id = s.qo_material_id
            WHERE  qo.status_code = 'Closed'
              AND  qo.closed_at >= DATEADD(day, -30, SYSUTCDATETIME())
              AND  qom.material_group IS NOT NULL
            GROUP  BY qom.material_group
            ORDER  BY AvgDefectPct DESC;";

        using var grid = await c.QueryMultipleAsync(sql, new
        {
            stale = thresholds.StaleArrivalDays,
            openDays = thresholds.OpenQoDays
        });

        var arrivalsByStatus = (await grid.ReadAsync<(string status_code, int Cnt)>())
            .ToDictionary(r => r.status_code, r => r.Cnt);
        var qoByStatus = (await grid.ReadAsync<(string status_code, int Cnt)>())
            .ToDictionary(r => r.status_code, r => r.Cnt);
        var trend       = (await grid.ReadAsync<TrendPoint>()).ToList();
        var counts      = await grid.ReadSingleAsync<(int StaleArrivals, int AgedOpenQos, int CompletedLast30, int PendingContainers)>();
        var avgDefect   = await grid.ReadFirstOrDefaultAsync<decimal?>();
        var openArr     = (await grid.ReadAsync<Arrival>()).ToList();
        var openQos     = (await grid.ReadAsync<QualityOrder>()).ToList();
        var throughput  = (await grid.ReadAsync<ThroughputPoint>()).ToList();
        var ageRow      = await grid.ReadSingleOrDefaultAsync<(int B0_3, int B3_7, int B7_14, int B14p)>();
        var defectGroup = (await grid.ReadAsync<DefectGroupPoint>()).ToList();

        var draft = arrivalsByStatus.TryGetValue(ArrivalStatus.Draft, out var d) ? d : 0;
        var qoOpen = (qoByStatus.TryGetValue("Initial",  out var i) ? i : 0)
                   + (qoByStatus.TryGetValue("Open",     out var o) ? o : 0)
                   + (qoByStatus.TryGetValue("Reopened", out var r) ? r : 0);

        return new Aggregates
        {
            ArrivalsByStatus = arrivalsByStatus,
            QoByStatus       = qoByStatus,
            ArrivalsTrend    = trend,
            AvgDefectPctLast30 = avgDefect,
            Counts = new DashboardCounts
            {
                ArrivalsDraft           = draft,
                ArrivalsCompletedLast30 = counts.CompletedLast30,
                QoOpen                  = qoOpen,
                StaleArrivals           = counts.StaleArrivals,
                AgedOpenQos             = counts.AgedOpenQos,
                PendingContainers       = counts.PendingContainers
            },
            OpenArrivals    = openArr,
            OpenQos         = openQos,
            ThroughputTrend = throughput,
            OpenQoAgeBuckets = new[] { ageRow.B0_3, ageRow.B3_7, ageRow.B7_14, ageRow.B14p },
            DefectByGroup    = defectGroup
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
        public int[]                       OpenQoAgeBuckets { get; set; } = new int[4];
        public List<DefectGroupPoint>      DefectByGroup    { get; set; } = new();
    }
}
