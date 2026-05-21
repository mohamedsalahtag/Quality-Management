using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Aggregation queries that feed the home dashboard. One <c>QueryMultiple</c>
/// covers five SELECTs in a single round trip; the recent-arrivals list and
/// the online-users list reuse the existing entity services so we don't
/// duplicate SQL. AlertConfig thresholds come from site settings so the
/// "stale" / "aged" counters reflect whatever the admin configured.
/// </summary>
public class DashboardService : IDashboardService
{
    private readonly string _cs;
    private readonly ISettingsService _settings;
    private readonly IArrivalService _arrivals;
    private readonly IDbService _db;

    public DashboardService(IConfiguration config, ISettingsService settings,
        IArrivalService arrivals, IDbService db)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _settings = settings;
        _arrivals = arrivals;
        _db = db;
    }

    public async Task<DashboardVm> GetSummaryAsync(CancellationToken ct = default)
    {
        var thresholds = await _settings.GetAlertConfigAsync();

        // Three independent reads in parallel: the aggregation query, the
        // recent-arrivals list, and the active-users list. Each opens its
        // own connection so it's safe to await concurrently.
        var aggregatesTask = LoadAggregatesAsync(thresholds);
        var recentTask     = _arrivals.ListAsync(null, null);
        var usersTask      = _db.ListUsersAsync(null, null, isActive: true);
        await Task.WhenAll(aggregatesTask, recentTask, usersTask);

        var agg     = aggregatesTask.Result;
        var recent  = recentTask.Result.Take(8).ToList();
        var online  = usersTask.Result.Where(u => u.IsOnline).ToList();

        return new DashboardVm
        {
            Counts             = agg.Counts,
            ArrivalsByStatus   = agg.ArrivalsByStatus,
            QoByStatus         = agg.QoByStatus,
            ArrivalsTrend      = agg.ArrivalsTrend,
            AvgDefectPctLast30 = agg.AvgDefectPctLast30,
            Thresholds         = thresholds,
            RecentArrivals     = recent,
            OnlineUserCount    = online.Count,
            OnlineUsers        = online.Take(10).ToList()
        };
    }

    private async Task<Aggregates> LoadAggregatesAsync(AlertConfig thresholds)
    {
        using var c = new SqlConnection(_cs);
        await c.OpenAsync();

        // Single QueryMultiple = one round trip with five result sets.
        // All filters use server-side UTC (SYSUTCDATETIME) for consistency
        // with how the rest of the schema stores timestamps.
        const string sql = @"
            SELECT status_code, COUNT(*) AS Cnt
            FROM   qms_arrival
            GROUP  BY status_code;

            SELECT status_code, COUNT(*) AS Cnt
            FROM   qms_quality_order
            GROUP  BY status_code;

            SELECT  DATEADD(week, DATEDIFF(week, 0, created_at), 0) AS WeekStart,
                    SUM(1)                                                                AS Created,
                    SUM(CASE WHEN completed_at IS NOT NULL THEN 1 ELSE 0 END)             AS Completed
            FROM    qms_arrival
            WHERE   created_at >= DATEADD(week, -12, SYSUTCDATETIME())
            GROUP   BY DATEADD(week, DATEDIFF(week, 0, created_at), 0)
            ORDER   BY 1;

            SELECT
                (SELECT COUNT(*) FROM qms_arrival
                  WHERE status_code = 'Draft'
                    AND created_at < DATEADD(day, -@stale, SYSUTCDATETIME()))                       AS StaleArrivals,
                (SELECT COUNT(*) FROM qms_quality_order
                  WHERE status_code IN ('Initial','Open','Reopened')
                    AND COALESCE(opened_at, created_at) < DATEADD(day, -@openDays, SYSUTCDATETIME())) AS AgedOpenQos,
                (SELECT COUNT(*) FROM qms_arrival
                  WHERE status_code = 'Completed'
                    AND completed_at >= DATEADD(day, -30, SYSUTCDATETIME()))                        AS CompletedLast30;

            SELECT AVG(CAST(sd.defect_percentage AS DECIMAL(9,4)))
            FROM   qms_sample_defect sd
            JOIN   qms_sample        s  ON s.sample_id        = sd.sample_id
            JOIN   qms_quality_order qo ON qo.quality_order_id = s.quality_order_id
            WHERE  qo.status_code = 'Closed'
              AND  qo.closed_at >= DATEADD(day, -30, SYSUTCDATETIME());";

        using var grid = await c.QueryMultipleAsync(sql, new
        {
            stale = thresholds.StaleArrivalDays,
            openDays = thresholds.OpenQoDays
        });

        var arrivalsByStatus = (await grid.ReadAsync<(string status_code, int Cnt)>())
            .ToDictionary(r => r.status_code, r => r.Cnt);
        var qoByStatus = (await grid.ReadAsync<(string status_code, int Cnt)>())
            .ToDictionary(r => r.status_code, r => r.Cnt);
        var trend = (await grid.ReadAsync<TrendPoint>()).ToList();
        var counts = await grid.ReadSingleAsync<(int StaleArrivals, int AgedOpenQos, int CompletedLast30)>();
        var avgDefect = await grid.ReadFirstOrDefaultAsync<decimal?>();

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
                AgedOpenQos             = counts.AgedOpenQos
            }
        };
    }

    private sealed class Aggregates
    {
        public DashboardCounts Counts                   { get; set; } = new();
        public Dictionary<string,int> ArrivalsByStatus  { get; set; } = new();
        public Dictionary<string,int> QoByStatus        { get; set; } = new();
        public List<TrendPoint> ArrivalsTrend           { get; set; } = new();
        public decimal? AvgDefectPctLast30              { get; set; }
    }
}
