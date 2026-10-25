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
    public void Weeks_are_iso_weeks_monday_to_sunday()
    {
        Assert.Equal(new DateTime(2026, 10, 19), DefectReportService.WeekStart(new DateTime(2026, 10, 19))); // Monday
        Assert.Equal(new DateTime(2026, 10, 19), DefectReportService.WeekStart(new DateTime(2026, 10, 25))); // Sunday
        Assert.Equal(new DateTime(2026, 10, 26), DefectReportService.WeekStart(new DateTime(2026, 10, 26)));
        Assert.Equal(43, System.Globalization.ISOWeek.GetWeekOfYear(new DateTime(2026, 10, 19)));
        var (from, to) = DefectReportsController.DefaultWindow();
        Assert.Equal(DayOfWeek.Monday, from.DayOfWeek);
        Assert.Equal(DateTime.Today, to);
    }

    /// <summary>Pack weight and cartons travel on every line, and the cartons
    /// are the PO quantity counted once per order and arrival line.</summary>
    [Fact]
    public async Task Lines_carry_pack_weight_and_cartons()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDefectReportService>();
        var data = await svc.GetDatasetAsync(From, To, PlantScope.All);
        if (data.Units.Count == 0) return;
        Assert.All(data.Units, u => Assert.InRange(Convert.ToInt32(u[12]), 0, data.PackWeights.Count - 1));
        Assert.Contains(data.PackWeights, w => w.EndsWith(" kg"));
        var cartons = data.Units.Sum(u => Convert.ToDecimal(u[13]));

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            scope.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        var orderIds = data.Units.Select(u => Convert.ToInt64(u[0])).Distinct().ToArray();
        decimal db = 0;
        foreach (var chunk in orderIds.Chunk(1000))
            db += await c.ExecuteScalarAsync<decimal>(@"
                SELECT ISNULL(SUM(q), 0) FROM (
                    SELECT DISTINCT m.quality_order_id, m.arrival_item_id, CAST(ISNULL(ai.quantity, 0) AS DECIMAL(18,3)) q
                    FROM   qms_quality_order_material m
                    JOIN   qms_arrival_item ai ON ai.arrival_item_id = m.arrival_item_id
                    WHERE  m.quality_order_id IN @ids
                      AND  EXISTS (SELECT 1 FROM qms_sample s WHERE s.qo_material_id = m.qo_material_id AND s.is_deleted = 0)) t",
                new { ids = chunk });
        _out.WriteLine($"{cartons:N0} cartons on {orderIds.Length} orders; {data.PackWeights.Count} pack weights");
        Assert.Equal(db, cartons);
    }

    [Fact]
    public async Task Pending_containers_match_the_dashboard()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var res = await client.GetAsync($"/DefectReports/Pending?from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

        using var scope = _factory.Services.CreateScope();
        var vm = await scope.ServiceProvider.GetRequiredService<IDashboardService>().GetSummaryAsync(
            new SharbatlyQMS.Web.ViewModels.DashboardFilter
            {
                Period = SharbatlyQMS.Web.ViewModels.DashboardFilter.Periods.Custom, From = From, To = To
            }, PlantScope.All, CancellationToken.None);
        _out.WriteLine($"{rows.GetArrayLength()} pending containers");
        Assert.Equal(vm.Commitment.Sum(x => x.Outstanding), rows.GetArrayLength());
    }

    private static SharbatlyQMS.Web.Services.DefectReportExportRequest SampleExport(string format) => new()
    {
        Format = format, Title = "Defects per branch", Tab = "Per branch & defect", Hint = "One row per branch.",
        Period = "Received 2026-09-01 to 2026-10-25", Filters = new() { "Major category: Apples" },
        Kpis = new() { new() { "Orders", "120" }, new() { "Cartons (PO qty)", "45,000" } },
        RowLabel = "Branch / sub-major",
        Columns = new()
        {
            new() { Group = "Major", Color = "#b02a37", Label = "Bruising", Kind = "defect" },
            new() { Group = "Major", Color = "#b02a37", Label = "Major total", Kind = "cattot" },
            new() { Group = "Minor", Color = "#ffc107", Label = "Russeting", Kind = "defect" },
            new() { Group = "Minor", Color = "#ffc107", Label = "Minor total", Kind = "cattot" },
            new() { Label = "All defects", Kind = "all" },
            new() { Label = "Cartons (PO qty)", Kind = "cartons" },
            new() { Label = "W43 vs W42", Kind = "trend" }
        },
        Rows = new()
        {
            new() { Label = "Jeddah (JD01)", Level = "grp", Values = new() { 2.5m, 2.5m, 0.4m, 0.4m, 2.9m, 12000m, 0.3m }, Trends = new() { null, null, null, null, null, null, null } },
            new() { Label = "Apples Royal Gala", Level = "child", Indent = 1, Values = new() { 3.1m, 3.1m, null, null, 3.1m, 8000m, -0.2m }, Trends = new() { 0.4m, null, null, null, null, null, null } },
            new() { Label = "Grand total", Level = "gt", Values = new() { 2.5m, 2.5m, 0.4m, 0.4m, 2.9m, 12000m, 0.3m }, Trends = new() { null, null, null, null, null, null, null } }
        },
        Generated = "2026-10-25 10:00"
    };

    [Fact]
    public void The_excel_export_has_headings_and_numeric_cells()
    {
        var bytes = SharbatlyQMS.Web.Services.DefectReportExporter.Excel(SampleExport("xlsx"));
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.First();
        Assert.Contains("Defects per branch", ws.Cell(1, 1).GetString());
        Assert.Contains("2026-09-01", ws.Cell(2, 1).GetString());
        var label = ws.CellsUsed().First(c => c.GetString() == "Apples Royal Gala");
        var bruising = ws.Cell(label.Address.RowNumber, 2);
        Assert.Equal(ClosedXML.Excel.XLDataType.Number, bruising.DataType);
        Assert.Equal(3.1, bruising.GetDouble(), 3);
    }

    [Fact]
    public void The_pdf_export_renders()
    {
        var bytes = SharbatlyQMS.Web.Services.DefectReportExporter.Pdf(SampleExport("pdf"));
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
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
