using System.Net;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Controllers;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Reports -> Defect reports, against live data.
///
/// The page pivots in the browser, so what the server can be held to is the
/// dataset it hands over: that it carries every defective unit and every
/// inspected unit of the window and nothing else, and that the drill-down
/// workbook recomputes the same figures the page shows.
/// </summary>
[Collection("workflow")]
public class DefectReportsTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public DefectReportsTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private static readonly DateTime From = DateTime.Today.AddDays(-28);
    private static readonly DateTime To   = DateTime.Today;

    [Fact]
    public void Weeks_run_saturday_to_friday()
    {
        Assert.Equal(new DateTime(2026, 10, 10), DefectReportService.WeekStart(new DateTime(2026, 10, 10))); // Saturday
        Assert.Equal(new DateTime(2026, 10, 10), DefectReportService.WeekStart(new DateTime(2026, 10, 16))); // Friday
        Assert.Equal(new DateTime(2026, 10, 17), DefectReportService.WeekStart(new DateTime(2026, 10, 17)));
        var (from, to) = DefectReportsController.DefaultWindow();
        Assert.Equal(DayOfWeek.Saturday, from.DayOfWeek);
        Assert.Equal(DateTime.Today, to);
    }

    /// <summary>The dataset's totals equal the database's, for finished live orders in the window.</summary>
    [Fact]
    public async Task The_dataset_carries_every_unit_of_the_window()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDefectReportService>();
        var data = await svc.GetDatasetAsync(From, To, PlantScope.All);

        var inspected = data.Units.Sum(u => Convert.ToDecimal(u[11]));
        var defective = data.Facts.Sum(f => Convert.ToDecimal(f[2]));
        var orders    = data.Units.Select(u => Convert.ToInt64(u[0])).Distinct().Count();
        _out.WriteLine($"{data.Units.Count} lines, {orders} orders, {inspected:N0} inspected, {defective:N0} defective");

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            scope.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        var db = await c.QuerySingleAsync<(int Orders, decimal Inspected, decimal Defective)>(@"
            WITH q AS (
                SELECT qo.quality_order_id
                FROM   qms_quality_order qo
                JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
                LEFT JOIN qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
                OUTER APPLY (SELECT TOP 1 c2.receive_date FROM qms_sap_container_cache c2
                             WHERE c2.container_no = a.container_no AND c2.bol_no = a.bol_no AND c2.ebeln = a.ebeln
                             ORDER BY c2.doc_date DESC, c2.sto) cc
                WHERE  qo.status_code = 'Closed' AND qo.superseded_at IS NULL
                  AND  COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) >= @from
                  AND  COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) <  @toEx)
            SELECT (SELECT COUNT(DISTINCT s.quality_order_id) FROM qms_sample s JOIN q ON q.quality_order_id = s.quality_order_id WHERE s.is_deleted = 0),
                   (SELECT ISNULL(SUM(CAST(ISNULL(s.sample_size,0) AS DECIMAL(18,2))),0) FROM qms_sample s JOIN q ON q.quality_order_id = s.quality_order_id WHERE s.is_deleted = 0),
                   (SELECT ISNULL(SUM(CAST(sd.defect_value AS DECIMAL(18,2))),0) FROM qms_sample_defect sd JOIN qms_sample s ON s.sample_id = sd.sample_id
                      JOIN q ON q.quality_order_id = s.quality_order_id WHERE s.is_deleted = 0)",
            new { from = From.Date, toEx = To.Date.AddDays(1) });

        Assert.Equal(db.Orders, orders);
        Assert.Equal(db.Inspected, inspected);
        Assert.Equal(db.Defective, defective);
    }

    /// <summary>
    /// The drill-down workbook's rows add up to the page's figure: one cell of
    /// the Per major tab (Major category of the biggest fruit), computed from
    /// the dataset the way the page does, against DrillAsync.
    /// </summary>
    [Fact]
    public async Task The_drill_down_reconciles_with_the_page_figure()
    {
        using var scope = _factory.Services.CreateScope();
        var svc  = scope.ServiceProvider.GetRequiredService<IDefectReportService>();
        var data = await svc.GetDatasetAsync(From, To, PlantScope.All);
        if (data.Units.Count == 0) return;

        var majorIdx = data.Units.GroupBy(u => Convert.ToInt32(u[4])).OrderByDescending(g => g.Count()).First().Key;
        var major = data.Majors[majorIdx];
        const string cat = "Major";

        // The page's rule, QC report %: defective units / inspected units of
        // lines whose fruit has a defect in the category.
        decimal num = 0, den = 0, pcs = 0, rows = 0;
        var catCount = data.GroupDefects.ToDictionary(kv => kv.Key,
            kv => kv.Value.Count(i => (string)data.Defects[i][2] == cat));
        var lines = data.Units.Select((u, i) => (u, i)).Where(x => Convert.ToInt32(x.u[4]) == majorIdx).ToList();
        var lineSet = lines.Select(x => x.i).ToHashSet();
        foreach (var (u, _) in lines)
        {
            var n = catCount[data.Groups[Convert.ToInt32(u[9])]];
            if (n == 0) continue;
            den  += Convert.ToDecimal(u[11]);
            rows += Convert.ToInt32(u[10]) * n;
        }
        foreach (var f in data.Facts.Where(f => lineSet.Contains(Convert.ToInt32(f[0])) && (string)data.Defects[Convert.ToInt32(f[1])][2] == cat))
        { num += Convert.ToDecimal(f[2]); pcs += Convert.ToDecimal(f[3]); }
        if (den == 0) return;

        var drill = await svc.DrillAsync(new DefectDrillRequest
        {
            From = From, To = To, Tab = "major", RowKey = major, DefectKey = "C:" + cat, Metric = "qc"
        }, PlantScope.All);
        var drillPct = 100m * drill.Sum(o => o.Defective) / drill.Sum(o => o.Inspected);
        _out.WriteLine($"{major} / {cat}: page {100m * num / den:0.000}% vs drill {drillPct:0.000}% over {drill.Count} orders; " +
                       $"Excel average {pcs / rows:0.000}%");
        Assert.Equal(num, drill.Sum(o => o.Defective));
        Assert.Equal(den, drill.Sum(o => o.Inspected));
        Assert.Equal(rows, drill.Sum(o => o.SampleDefectRows));
        Assert.Equal(pcs, drill.Sum(o => o.PctSum));
        Assert.True(drill.Zip(drill.Skip(1)).All(p => (p.First.Percent ?? -1) >= (p.Second.Percent ?? -1)),
            "The drill-down is not sorted by highest % first.");
    }

    [Fact]
    public async Task The_page_the_data_and_the_workbook_load()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();

        var page = await client.GetAsync("/DefectReports");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("defect-reports.js", await page.Content.ReadAsStringAsync());

        var data = await client.GetAsync($"/DefectReports/Data?from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, data.StatusCode);
        var json = await data.Content.ReadAsStringAsync();
        // The browser reads these exact names; material-group keys must keep their case.
        foreach (var key in new[] { "\"units\"", "\"facts\"", "\"defects\"", "\"groupDefects\"", "\"categories\"", "\"nm\"" })
            Assert.Contains(key, json);

        var xlsx = await client.GetAsync($"/DefectReports/DrillExcel?From={From:yyyy-MM-dd}&To={To:yyyy-MM-dd}&Tab=major&RowKey=__all&DefectKey=T&Metric=qc");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        var bytes = await xlsx.Content.ReadAsByteArrayAsync();
        Assert.True(bytes[0] == 0x50 && bytes[1] == 0x4B, "The drill export is not an xlsx.");
    }

    [Fact]
    public async Task A_plant_restricted_user_gets_only_their_plant()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDefectReportService>();
        var all = await svc.GetDatasetAsync(From, To, PlantScope.All);
        if (all.Plants.Count < 2) return;
        var plant = all.Plants[0];
        var mine = await svc.GetDatasetAsync(From, To, new PlantScope(false, new[] { plant }));
        Assert.Equal(new[] { plant }, mine.Plants);
    }
}
