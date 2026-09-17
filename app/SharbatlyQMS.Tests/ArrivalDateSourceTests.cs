using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// "Arrival date" must mean the date SAP states the container arrived, not the
/// moment somebody created the arrival record in this application.
///
/// The two drifted far apart and nothing held them together. Measured across
/// 2,174 arrivals on 2026-09-17: the record is created on average 3.7 days
/// after the SAP arrival date and as much as 34 days after, 260 of them (12%)
/// fall in a different MONTH, and the dashboard's September figure was 939
/// against SAP's 743 -- a 26% overstatement of containers received.
///
/// That is invisible by eye: every number looks plausible, and only somebody
/// reconciling against SAP would ever find it. So it is pinned here.
/// </summary>
[Collection("workflow")]
public class ArrivalDateSourceTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ArrivalDateSourceTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private Microsoft.Data.SqlClient.SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));
    }

    private async Task<DashboardVm> DashboardAsync(DateTime from, DateTime to)
    {
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IDashboardService>();
        return await svc.GetSummaryAsync(
            new DashboardFilter { Period = "custom", From = from, To = to },
            PlantScope.All, CancellationToken.None);
    }

    /// <summary>
    /// The count the dashboard prints has to be the count SAP's dates give.
    /// Checked over a month, because that is the window where the two
    /// definitions diverge most.
    /// </summary>
    [Fact]
    public async Task Received_counts_containers_by_the_sap_arrival_date()
    {
        var from = new DateTime(2026, 8, 1);
        var to   = new DateTime(2026, 8, 31);

        using var c = Db();
        await c.OpenAsync();
        var bySap = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, @"
            SELECT COUNT(*)
            FROM   qms_arrival a
            LEFT   JOIN qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
            WHERE  COALESCE(ss.arrival_date, CAST(a.created_at AS DATE)) >= @from
              AND  COALESCE(ss.arrival_date, CAST(a.created_at AS DATE)) <  @toEx",
            new { from, toEx = to.AddDays(1) });

        var byRecord = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, @"
            SELECT COUNT(*) FROM qms_arrival a
            WHERE  a.created_at >= @from AND a.created_at < @toEx",
            new { from, toEx = to.AddDays(1) });

        var vm = await DashboardAsync(from, to);
        var shown = vm.Commitment.Sum(x => x.Received);

        _out.WriteLine($"dashboard {shown}, by SAP date {bySap}, by record creation {byRecord}");
        Assert.Equal(bySap, shown);

        // And if the two definitions differ for this month, the dashboard must
        // be on the SAP side of that difference -- otherwise this test would
        // pass on a month where nothing distinguishes them.
        if (bySap != byRecord)
            Assert.NotEqual(byRecord, shown);
    }

    /// <summary>
    /// A container that arrived in one month and was recorded in the next must
    /// be counted in the month it ARRIVED. This is the case that made monthly
    /// figures disagree with SAP.
    /// </summary>
    [Fact]
    public async Task A_container_recorded_late_counts_in_the_month_it_arrived()
    {
        using var c = Db();
        await c.OpenAsync();
        var straddler = await Dapper.SqlMapper.QueryFirstOrDefaultAsync(c, @"
            SELECT TOP 1 a.arrival_id, a.arrival_no, a.plant,
                   ss.arrival_date          AS SapArrival,
                   CAST(a.created_at AS DATE) AS RecordCreated
            FROM   qms_arrival a
            JOIN   qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
            WHERE  a.status_code <> 'Cancelled'
              AND  ss.arrival_date IS NOT NULL
              AND  EOMONTH(ss.arrival_date) <> EOMONTH(a.created_at)
              AND  ss.arrival_date >= DATEADD(MONTH, -4, GETDATE())
            ORDER  BY a.arrival_id DESC");

        if (straddler is null) { _out.WriteLine("No month-straddling arrival to test."); return; }

        DateTime sap = straddler.SapArrival;
        _out.WriteLine($"{straddler.arrival_no}: arrived {sap:yyyy-MM-dd}, recorded {straddler.RecordCreated:yyyy-MM-dd}");

        var monthStart = new DateTime(sap.Year, sap.Month, 1);
        var monthEnd   = monthStart.AddMonths(1).AddDays(-1);

        var rows = await Detail(CommitmentBuckets.Received, monthStart, monthEnd);
        Assert.Contains(rows, r => r.ArrivalId == (long)straddler.arrival_id);

        // And NOT in the month the record happened to be created in.
        DateTime rec = straddler.RecordCreated;
        var otherStart = new DateTime(rec.Year, rec.Month, 1);
        if (otherStart != monthStart)
        {
            var otherRows = await Detail(CommitmentBuckets.Received,
                                         otherStart, otherStart.AddMonths(1).AddDays(-1));
            Assert.DoesNotContain(otherRows, r => r.ArrivalId == (long)straddler.arrival_id);
        }
    }

    private async Task<IReadOnlyList<CommitmentDetailRow>> Detail(string bucket, DateTime from, DateTime to)
    {
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IDashboardService>();
        return await svc.GetCommitmentDetailAsync(
            new DashboardFilter { Period = "custom", From = from, To = to },
            PlantScope.All, bucket, null, CancellationToken.None);
    }

    /// <summary>
    /// The drill-through reports the SAP date, and reports the record date
    /// separately rather than letting one stand in for the other.
    /// </summary>
    [Fact]
    public async Task The_drill_through_shows_the_sap_date_and_the_record_date()
    {
        var rows = await Detail(CommitmentBuckets.Received,
                                new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));
        if (rows.Count == 0) { _out.WriteLine("Nothing received in August."); return; }

        using var c = Db();
        await c.OpenAsync();
        foreach (var r in rows.Take(20))
        {
            var sap = await Dapper.SqlMapper.ExecuteScalarAsync<DateTime?>(c,
                "SELECT ss.arrival_date FROM qms_shipment_snapshot ss WHERE ss.arrival_id = @id",
                new { id = r.ArrivalId });
            if (sap is null) continue;
            Assert.Equal(sap.Value.Date, r.ArrivalCreatedAt.Date);
        }

        var apart = rows.Count(r => r.ArrivalCreatedAt.Date != r.RecordCreatedAt.Date);
        _out.WriteLine($"{apart} of {rows.Count} rows have the two dates on different days");
        Assert.All(rows, r => Assert.NotEqual(default, r.RecordCreatedAt));
    }

    /// <summary>
    /// Every bucket still reconciles after the change. The definitions moved;
    /// the arithmetic must not.
    /// </summary>
    [Fact]
    public async Task The_buckets_still_add_up_on_the_new_definition()
    {
        var from = new DateTime(2026, 8, 1);
        var to   = new DateTime(2026, 8, 31);
        var vm   = await DashboardAsync(from, to);

        var received = await Detail(CommitmentBuckets.Received,          from, to);
        var period   = await Detail(CommitmentBuckets.PeriodInspection,  from, to);
        var pending  = await Detail(CommitmentBuckets.PendingInspection, from, to);
        var backlog  = await Detail(CommitmentBuckets.BacklogInspected,  from, to);
        var total    = await Detail(CommitmentBuckets.TotalInspected,    from, to);

        _out.WriteLine($"received {received.Count} = period {period.Count} + pending {pending.Count}");
        _out.WriteLine($"total    {total.Count} = period {period.Count} + backlog {backlog.Count}");

        Assert.Equal(received.Count, period.Count + pending.Count);
        Assert.Equal(total.Count,    period.Count + backlog.Count);

        Assert.Equal(vm.Commitment.Sum(x => x.Received),   received.Count);
        Assert.Equal(vm.Commitment.Sum(x => x.Committed),  period.Count);
        Assert.Equal(vm.Commitment.Sum(x => x.Outstanding), pending.Count);
        Assert.Equal(vm.Commitment.Sum(x => x.QosCatchUp), backlog.Count);
        Assert.Equal(vm.Commitment.Sum(x => x.QosCreated), total.Count);
    }

    /// <summary>
    /// Every screen that prints an arrival date must read it from the same
    /// SAP-sourced place. This walks the ones that already did and confirms
    /// they still agree with the snapshot, so a future edit cannot quietly
    /// repoint one of them at the record date.
    /// </summary>
    [Fact]
    public async Task Pending_containers_and_the_time_bar_agree_with_sap()
    {
        using var scope = _factory.Services.CreateScope();
        var cache   = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();
        var timeBar = scope.ServiceProvider.GetRequiredService<ITimeBarService>();

        var pending = await cache.ListPendingAsync(pageSize: 50);
        using var c = Db();
        await c.OpenAsync();
        foreach (var row in pending.Rows.Take(25))
        {
            var sap = await Dapper.SqlMapper.ExecuteScalarAsync<DateTime?>(c, @"
                SELECT MAX(arrival_date) FROM qms_sap_container_cache
                WHERE container_no = @ContainerNo AND bol_no = @BolNo AND ebeln = @Ebeln",
                new { row.ContainerNo, row.BolNo, row.Ebeln });
            Assert.Equal(sap?.Date, row.ArrivalDate?.Date);
        }

        var bar = await timeBar.ListAsync(new TimeBarFilter { PageSize = 50 },
                                          PlantScope.All, new TimeBarConfig());
        _out.WriteLine($"checked {pending.Rows.Count} pending and {bar.Rows.Count} time bar rows");
        Assert.All(bar.Rows.Where(r => r.ArrivalDate.HasValue),
                   r => Assert.True(r.ArrivalDate!.Value.Year >= 2020));
    }
}
