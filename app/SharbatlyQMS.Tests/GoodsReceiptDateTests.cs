using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// One receive date per container, on every screen.
///
/// SAP's Receive_Date moves: it appears when the container is pulled out of
/// the port and is advanced to the day the branch books the goods in, days
/// later, on more than half of all arrivals. The arrival snapshot used to copy
/// it once and never again, so the pending list and the Time Bar page (live
/// cache) disagreed with the arrival page, the claims list, the QC report and
/// the dashboard (frozen snapshot) about when a container was received. Now
/// the sweep refreshes the snapshot, and these tests hold every surface to the
/// same date -- against the live database, because that is where the drift
/// happened.
/// </summary>
[Collection("workflow")]
public class GoodsReceiptDateTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public GoodsReceiptDateTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new SqlConnection(cfg.GetConnectionString("Default"));
    }

    private sealed record Sample(long ArrivalId, long QualityOrderId, string ContainerNo, string BolNo, string Ebeln,
                                 string Plant, DateTime? SnapshotReceive, DateTime? CacheReceive, DateTime? ClosedAt);

    /// <summary>Recent finished orders whose container the SAP cache knows.</summary>
    private async Task<List<Sample>> SamplesAsync(int take = 25)
    {
        using var c = Db();
        await c.OpenAsync();
        var rows = await c.QueryAsync<Sample>($@"
            SELECT TOP {take}
                   a.arrival_id        AS ArrivalId,
                   qo.quality_order_id AS QualityOrderId,
                   ISNULL(a.container_no, '') AS ContainerNo,
                   ISNULL(a.bol_no, '')       AS BolNo,
                   ISNULL(a.ebeln, '')        AS Ebeln,
                   a.plant             AS Plant,
                   ss.receive_date     AS SnapshotReceive,
                   k.receive_date      AS CacheReceive,
                   qo.closed_at        AS ClosedAt
            FROM   qms_arrival a
            JOIN   qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
            JOIN   qms_quality_order qo ON qo.arrival_id = a.arrival_id
                                        AND qo.superseded_at IS NULL AND qo.closed_at IS NOT NULL
            CROSS APPLY (SELECT MAX(receive_date) AS receive_date
                         FROM   qms_sap_container_cache cc
                         WHERE  cc.container_no = ISNULL(a.container_no, '')
                           AND  cc.bol_no       = ISNULL(a.bol_no, '')
                           AND  cc.ebeln        = ISNULL(a.ebeln, '')) k
            WHERE  a.status_code <> 'Cancelled' AND k.receive_date IS NOT NULL
            ORDER  BY qo.closed_at DESC");
        return rows.ToList();
    }

    /// <summary>
    /// The refresh the sweep runs. After it, no arrival the cache knows may
    /// carry a receive date other than SAP's current one, and the legacy twin
    /// column may not disagree with it.
    /// </summary>
    [Fact]
    public async Task The_snapshot_carries_saps_current_receive_date()
    {
        using var scope = _factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();
        var changed = await cache.RefreshArrivalSnapshotsAsync();
        _out.WriteLine($"refresh changed {changed} snapshot(s)");

        using var c = Db();
        await c.OpenAsync();
        var behind = await c.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM   qms_shipment_snapshot ss
            JOIN   qms_arrival a ON a.arrival_id = ss.arrival_id
            CROSS APPLY (SELECT MAX(receive_date) AS receive_date, MAX(port_arrival_date) AS port_arrival_date
                         FROM   qms_sap_container_cache cc
                         WHERE  cc.container_no = ISNULL(a.container_no, '')
                           AND  cc.bol_no       = ISNULL(a.bol_no, '')
                           AND  cc.ebeln        = ISNULL(a.ebeln, '')) k
            WHERE (k.receive_date IS NOT NULL
                   AND (ss.receive_date <> k.receive_date OR ss.arrival_date <> k.receive_date
                        OR ss.receive_date IS NULL OR ss.arrival_date IS NULL))
               OR (k.port_arrival_date IS NOT NULL
                   AND (ss.port_arrival_date IS NULL OR ss.port_arrival_date <> k.port_arrival_date))");
        Assert.Equal(0, behind);

        // And running it again changes nothing: it is safe on every sweep.
        Assert.Equal(0, await cache.RefreshArrivalSnapshotsAsync());
    }

    /// <summary>
    /// The crux of the complaint: the QC report's Time Bar and the Time Bar
    /// page's clock must be the same number for the same container.
    /// </summary>
    [Fact]
    public async Task The_report_time_bar_equals_the_time_bar_page()
    {
        var samples = await SamplesAsync();
        if (samples.Count == 0) { _out.WriteLine("No finished orders with a cached container."); return; }

        using var scope = _factory.Services.CreateScope();
        var timeBar  = scope.ServiceProvider.GetRequiredService<ITimeBarService>();
        var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();
        var cfg      = new TimeBarConfig { ArrivalBasis = TimeBarArrivalBases.GoodsReceipt };

        var checkedCount = 0;
        foreach (var s in samples.Take(10))
        {
            var page = await timeBar.ListAsync(
                new TimeBarFilter { Container = s.ContainerNo, Bol = s.BolNo, Po = s.Ebeln, IncludeArchived = true, PageSize = 50 },
                PlantScope.All, cfg);
            var row = page.Rows.FirstOrDefault(r => r.ContainerNo == s.ContainerNo && r.BolNo == s.BolNo && r.Ebeln == s.Ebeln);
            if (row is null) continue;

            // What the report prints: the snapshot's receive date to the local
            // finish date, through the one shared helper.
            var shipment = await arrivals.GetShipmentAsync(s.ArrivalId);
            Assert.NotNull(shipment);
            var report = new SharbatlyQMS.Web.Services.Pdf.QualityReportData
            {
                Shipment     = shipment,
                QualityOrder = new QualityOrder { ClosedAt = s.ClosedAt },
                TimeBarBasis = cfg.ArrivalBasis
            };

            _out.WriteLine($"{s.ContainerNo}: page {row.ElapsedDays} d from {row.ArrivalDate:yyyy-MM-dd}, " +
                           $"report {report.TimeBarDays} d from {report.TimeBarStartDate:yyyy-MM-dd}");
            Assert.Equal(row.ArrivalDate?.Date, report.TimeBarStartDate?.Date);
            Assert.Equal(row.ElapsedDays, report.TimeBarDays);
            checkedCount++;
        }
        Assert.True(checkedCount > 0, "no Time Bar row matched any sampled container");
    }

    /// <summary>
    /// The arrival page, the claims list, the dashboard drill-through and the
    /// Data Hub all print SAP's current receive date -- the one the pending
    /// list and the Time Bar page show.
    /// </summary>
    [Fact]
    public async Task Every_surface_prints_the_same_receive_date()
    {
        var samples = await SamplesAsync();
        if (samples.Count == 0) { _out.WriteLine("No finished orders with a cached container."); return; }

        using var scope = _factory.Services.CreateScope();
        var arrivals  = scope.ServiceProvider.GetRequiredService<IArrivalService>();
        var claims    = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var dashboard = scope.ServiceProvider.GetRequiredService<IDashboardService>();

        using var c = Db();
        await c.OpenAsync();

        foreach (var s in samples.Take(8))
        {
            var expected = s.CacheReceive!.Value.Date;

            // Arrival page / QO page / QC report all read GetShipmentAsync.
            var shipment = await arrivals.GetShipmentAsync(s.ArrivalId);
            Assert.Equal(expected, shipment!.ReceiveDate?.Date);
            Assert.Equal(expected, shipment.ArrivalDate?.Date);

            // Claims list.
            var claim = (await claims.ListClosedQosAsync(
                new ClaimListFilter { Container = s.ContainerNo }, PlantScope.All, "test")).Rows
                .FirstOrDefault(r => r.QualityOrderId == s.QualityOrderId);
            if (claim != null) Assert.Equal(expected, claim.ArrivalDate?.Date);

            // Dashboard: the container is counted in the month it was received.
            var monthStart = new DateTime(expected.Year, expected.Month, 1);
            var received = await dashboard.GetCommitmentDetailAsync(
                new DashboardFilter { Period = "custom", From = monthStart, To = monthStart.AddMonths(1).AddDays(-1) },
                PlantScope.All, CommitmentBuckets.Received, null, CancellationToken.None);
            var mine = received.FirstOrDefault(r => r.ArrivalId == s.ArrivalId);
            Assert.NotNull(mine);
            Assert.Equal(expected, mine!.ArrivalCreatedAt.Date);

            // Data Hub flat view.
            var flat = await c.ExecuteScalarAsync<DateTime?>(
                "SELECT TOP 1 ReceiveDate FROM vw_qms_flat_defects WHERE ArrivalId = @id", new { id = s.ArrivalId });
            if (flat.HasValue) Assert.Equal(expected, flat.Value.Date);
        }
    }

    /// <summary>
    /// Transit days are loading to the inspector's discharge date, and the
    /// Data Hub agrees with the arrival page about it.
    /// </summary>
    [Fact]
    public async Task Transit_days_are_loading_to_discharge_everywhere()
    {
        using var c = Db();
        await c.OpenAsync();
        var rows = (await c.QueryAsync<(long ArrivalId, DateTime Sailing, DateTime Discharge, short? Sap)>(@"
            SELECT TOP 10 ss.arrival_id, ss.sailing_date, ss.discharge_date, ss.transit_days
            FROM   qms_shipment_snapshot ss
            JOIN   qms_arrival a ON a.arrival_id = ss.arrival_id
            WHERE  ss.sailing_date IS NOT NULL AND ss.discharge_date IS NOT NULL
              AND  a.status_code <> 'Cancelled'
            ORDER  BY ss.arrival_id DESC")).ToList();
        if (rows.Count == 0) { _out.WriteLine("No arrival with both dates."); return; }

        using var scope = _factory.Services.CreateScope();
        var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();

        foreach (var r in rows)
        {
            var expected = (short)Math.Max(0, (r.Discharge.Date - r.Sailing.Date).Days);
            var shipment = await arrivals.GetShipmentAsync(r.ArrivalId);
            Assert.Equal(expected, shipment!.EffectiveTransitDays);

            var flat = await c.ExecuteScalarAsync<short?>(
                "SELECT TOP 1 TransitDays FROM vw_qms_flat_defects WHERE ArrivalId = @id", new { id = r.ArrivalId });
            if (flat.HasValue) Assert.Equal(expected, flat.Value);

            _out.WriteLine($"arrival {r.ArrivalId}: {r.Sailing:yyyy-MM-dd} -> {r.Discharge:yyyy-MM-dd} = {expected} d (SAP said {r.Sap})");
        }
    }

    /// <summary>
    /// Under the receipt basis a container the branch has not booked in has no
    /// clock -- it is not quietly measured from the vessel's arrival instead.
    /// </summary>
    [Fact]
    public async Task A_container_without_a_receive_date_has_no_receipt_clock()
    {
        using var scope = _factory.Services.CreateScope();
        var timeBar = scope.ServiceProvider.GetRequiredService<ITimeBarService>();
        var page = await timeBar.ListAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true },
                                           PlantScope.All, new TimeBarConfig { ArrivalBasis = TimeBarArrivalBases.GoodsReceipt });
        Assert.All(page.Rows, r =>
        {
            if (r.ArrivalDate is null) Assert.Null(r.ElapsedDays);
            else Assert.NotEqual("Port", r.ArrivalSource);
        });
    }
}
