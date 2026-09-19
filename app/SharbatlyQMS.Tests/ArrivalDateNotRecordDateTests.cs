using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The standing guarantee: nothing anywhere treats qms_arrival.created_at --
/// the moment an arrival record was typed into this application -- as the date
/// the container arrived.
///
/// The two are days apart (3.7 on average, up to 34) and a tenth of them fall
/// in different months, so a screen keyed on the wrong one still looks
/// plausible and only disagrees with SAP. Checking each screen individually
/// would not survive a new screen being added, so this works the other way
/// round: it picks arrivals where the two dates are FAR apart and insists every
/// surface puts them in the period SAP says.
/// </summary>
[Collection("workflow")]
public class ArrivalDateNotRecordDateTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ArrivalDateNotRecordDateTests(QmsAppFactory factory, ITestOutputHelper output)
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

    private sealed record Straddler(long ArrivalId, string ArrivalNo, string Plant,
                                    DateTime SapArrival, DateTime RecordCreated);

    /// <summary>Arrivals whose SAP date and record date fall in different months.</summary>
    private async Task<List<Straddler>> StraddlersAsync(int take = 25)
    {
        using var c = Db();
        await c.OpenAsync();
        var rows = await Dapper.SqlMapper.QueryAsync<Straddler>(c, $@"
            SELECT TOP {take}
                   a.arrival_id               AS ArrivalId,
                   a.arrival_no               AS ArrivalNo,
                   a.plant                    AS Plant,
                   ss.arrival_date            AS SapArrival,
                   CAST(a.created_at AS DATE) AS RecordCreated
            FROM   qms_arrival a
            JOIN   qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
            WHERE  a.status_code <> 'Cancelled'
              AND  ss.arrival_date IS NOT NULL
              AND  EOMONTH(ss.arrival_date) <> EOMONTH(a.created_at)
            ORDER  BY a.arrival_id DESC");
        return rows.ToList();
    }

    private async Task<IReadOnlyList<CommitmentDetailRow>> ReceivedInMonthAsync(DateTime anyDayInMonth)
    {
        var from = new DateTime(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var to   = from.AddMonths(1).AddDays(-1);
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IDashboardService>();
        return await svc.GetCommitmentDetailAsync(
            new DashboardFilter { Period = "custom", From = from, To = to },
            PlantScope.All, CommitmentBuckets.Received, null, CancellationToken.None);
    }

    /// <summary>
    /// The dashboard counts a late-recorded container in the month it arrived,
    /// and NOT in the month somebody got round to writing it up.
    /// </summary>
    [Fact]
    public async Task No_dashboard_period_follows_the_record_date()
    {
        var straddlers = await StraddlersAsync();
        if (straddlers.Count == 0) { _out.WriteLine("No month-straddling arrivals."); return; }

        var checkedCount = 0;
        foreach (var x in straddlers.Take(8))
        {
            var arrivedMonth = await ReceivedInMonthAsync(x.SapArrival);
            Assert.Contains(arrivedMonth, r => r.ArrivalId == x.ArrivalId);

            var recordMonth = await ReceivedInMonthAsync(x.RecordCreated);
            Assert.DoesNotContain(recordMonth, r => r.ArrivalId == x.ArrivalId);

            _out.WriteLine($"{x.ArrivalNo}: arrived {x.SapArrival:yyyy-MM}, recorded {x.RecordCreated:yyyy-MM} " +
                           "-> counted in the arrival month only");
            checkedCount++;
        }
        Assert.True(checkedCount > 0);
    }

    /// <summary>
    /// The date the drill-through and the workbook print is SAP's, and the
    /// record date is shown separately rather than standing in for it.
    /// </summary>
    [Fact]
    public async Task Every_reported_arrival_date_is_the_sap_one()
    {
        var straddlers = await StraddlersAsync();
        if (straddlers.Count == 0) { _out.WriteLine("No month-straddling arrivals."); return; }

        var x = straddlers[0];
        var rows = await ReceivedInMonthAsync(x.SapArrival);
        var mine = rows.FirstOrDefault(r => r.ArrivalId == x.ArrivalId);
        Assert.NotNull(mine);

        _out.WriteLine($"{x.ArrivalNo}: reported arrival {mine!.ArrivalCreatedAt:yyyy-MM-dd}, " +
                       $"record {mine.RecordCreatedAt:yyyy-MM-dd}, SAP {x.SapArrival:yyyy-MM-dd}");
        Assert.Equal(x.SapArrival.Date, mine.ArrivalCreatedAt.Date);
        Assert.Equal(x.RecordCreated.Date, mine.RecordCreatedAt.ToLocalTime().Date);
        Assert.NotEqual(mine.ArrivalCreatedAt.Date, mine.RecordCreatedAt.ToLocalTime().Date);
    }

    /// <summary>
    /// Stale Arrivals asks how long ago the CONTAINER arrived, not how long the
    /// record has existed. A container that landed weeks ago and was written up
    /// yesterday is precisely the one the count exists to surface.
    /// </summary>
    [Fact]
    public async Task Stale_arrivals_measures_from_the_container_arriving()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var svc      = scope.ServiceProvider.GetRequiredService<IDashboardService>();
        var stale    = (await settings.GetAlertConfigAsync()).StaleArrivalDays;

        using var c = Db();
        await c.OpenAsync();
        var bySap = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, $@"
            SELECT COUNT(*)
            FROM   qms_arrival a
            LEFT   JOIN qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
            WHERE  a.status_code = 'Draft'
              AND  COALESCE(ss.arrival_date, CAST(a.created_at AS DATE))
                   < CAST(DATEADD(day, -{stale}, SYSUTCDATETIME()) AS DATE)");

        var vm = await svc.GetSummaryAsync(new DashboardFilter { Period = "month" },
                                           PlantScope.All, CancellationToken.None);
        _out.WriteLine($"stale threshold {stale} day(s): dashboard {vm.Counts.StaleArrivals}, by SAP date {bySap}");
        Assert.Equal(bySap, vm.Counts.StaleArrivals);
    }

    /// <summary>
    /// The screens that already read SAP still do. Cheap to check and it stops
    /// a future edit quietly repointing one of them at the record date.
    /// </summary>
    [Fact]
    public async Task The_other_surfaces_still_read_the_sap_date()
    {
        using var scope = _factory.Services.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

        using var c = Db();
        await c.OpenAsync();

        // Claims list.
        var claimRows = (await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test")).Rows;
        var seen = 0;
        foreach (var r in claimRows.Take(15))
        {
            if (r.ArrivalDate is null) continue;
            var sap = await Dapper.SqlMapper.ExecuteScalarAsync<DateTime?>(c, @"
                SELECT ss.arrival_date
                FROM   qms_shipment_snapshot ss
                JOIN   qms_quality_order qo ON qo.arrival_id = ss.arrival_id
                WHERE  qo.quality_order_id = @id", new { id = r.QualityOrderId });
            if (sap is null) continue;
            Assert.Equal(sap.Value.Date, r.ArrivalDate.Value.Date);
            seen++;
        }
        _out.WriteLine($"claims rows checked: {seen}");
    }
}
