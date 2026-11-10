using System.Reflection;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Controllers;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Pdf;
using SharbatlyQMS.Web.ViewModels;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The Branch on the QC report, the sent / not-sent filter on Quality Orders,
/// and the Defect reports drill-down for the second-level rows and the
/// multi-select filters. Against live data; nothing is left behind.
/// </summary>
[Collection("workflow")]
public class DefectReportsRound3Tests : IClassFixture<QmsAppFactory>
{
    private static readonly DateTime From = DateTime.Today.AddDays(-56), To = DateTime.Today;
    private readonly QmsAppFactory _factory;

    public DefectReportsRound3Tests(QmsAppFactory factory) => _factory = factory;

    private static Microsoft.Data.SqlClient.SqlConnection Db(IServiceScope s) => new(
        s.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

    /// <summary>QO-2026-004614: Argel's account says Al Hasa, the container is at
    /// Medinah, and the report printed "Branch Al Hasa".</summary>
    [Fact]
    public async Task The_report_branch_is_the_orders_plant_not_the_creators()
    {
        using var scope = _factory.Services.CreateScope();
        using var db = Db(scope);
        var id = await db.ExecuteScalarAsync<long?>(@"
            SELECT TOP 1 qo.quality_order_id
            FROM   qms_quality_order qo
            JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
            JOIN   qms.AppUser u ON u.Username = qo.created_by
            WHERE  qo.status_code = 'Closed' AND NULLIF(u.PlantCode, '') IS NOT NULL AND u.PlantCode <> a.plant
            ORDER  BY qo.quality_order_id DESC");
        if (id is null) return;

        var qos = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var codes = scope.ServiceProvider.GetRequiredService<ICodeDescriptionDirectory>();
        var qo = await qos.GetAsync(id.Value);

        var controller = ActivatorUtilities.CreateInstance<ReportsController>(scope.ServiceProvider);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider } };
        var build = typeof(ReportsController).GetMethod("BuildDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<QualityReportData>)build.Invoke(controller, new object?[] { qo })!;
        var data = await task;

        Assert.Equal(codes.PlantDisplay(qo!.Plant), data.Branch);
    }

    [Fact]
    public async Task The_sent_filter_splits_sent_from_finished_and_not_sent()
    {
        using var scope = _factory.Services.CreateScope();
        using var db = Db(scope);
        var qos = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var before = await db.ExecuteScalarAsync<long>("SELECT ISNULL(MAX(send_id), 0) FROM qms_qo_report_send");
        var id = await db.ExecuteScalarAsync<long?>(@"
            SELECT TOP 1 qo.quality_order_id FROM qms_quality_order qo
            WHERE  qo.status_code = 'Closed' AND qo.superseded_at IS NULL AND qo.archived_at IS NULL
              AND  NOT EXISTS (SELECT 1 FROM qms_qo_report_send s WHERE s.quality_order_id = qo.quality_order_id)
            ORDER  BY qo.created_at DESC, qo.quality_order_id DESC");
        if (id is null) return;

        var notSent = await qos.ListAsync(new QoListFilter { Sent = "notsent", PageSize = 100 }, PlantScope.All);
        Assert.Contains(notSent.Rows, r => r.QualityOrderId == id);
        Assert.All(notSent.Rows, r =>
        {
            Assert.Equal(QualityOrderStatus.Closed, r.StatusCode);
            Assert.Null(r.ReportSentAt);
        });

        try
        {
            await db.ExecuteAsync(@"
                INSERT INTO qms_qo_report_send (quality_order_id, sent_at, sent_by, to_addresses, attachment_count)
                VALUES (@id, SYSUTCDATETIME(), 'test', 'qa-supplier@example.com', 0)", new { id });

            var sent = await qos.ListAsync(new QoListFilter { Sent = "sent", PageSize = 100 }, PlantScope.All);
            Assert.Contains(sent.Rows, r => r.QualityOrderId == id);
            Assert.All(sent.Rows, r => Assert.NotNull(r.ReportSentAt));

            var after = await qos.ListAsync(new QoListFilter { Sent = "notsent", PageSize = 100 }, PlantScope.All);
            Assert.DoesNotContain(after.Rows, r => r.QualityOrderId == id);
        }
        finally
        {
            await db.ExecuteAsync("DELETE FROM qms_qo_report_send WHERE send_id > @before", new { before });
        }
    }

    [Fact]
    public async Task Every_new_row_key_and_the_multi_select_filters_reach_the_right_orders()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDefectReportService>();
        var d = await svc.GetDatasetAsync(From, To, PlantScope.All);
        if (d.Units.Count == 0) return;

        var u = d.Units[0];
        string Name(List<string> list, int i) => list[Convert.ToInt32(u[i])];
        var plant = Name(d.Plants, 2); var supplier = Name(d.Suppliers, 3); var sub = Name(d.Subs, 5);
        var variety = Name(d.Varieties, 15);
        var subKey = sub.Length == 0 ? "(none)" : sub; var varKey = variety.Length == 0 ? "(no variety)" : variety;
        var qoNo = (string)u[1];

        async Task<IReadOnlyList<DefectDrillRow>> Drill(string tab, string rowKey) =>
            await svc.DrillAsync(new DefectDrillRequest { From = From, To = To, Tab = tab, RowKey = rowKey, DefectKey = "T" }, PlantScope.All);

        // The line itself is under each key its tab builds for it.
        var branchChild = await Drill("branch", $"{sub}\u0002{plant}");
        Assert.Contains(branchChild, o => o.QualityOrderNo == qoNo);
        Assert.All(branchChild, o => Assert.Equal(plant, o.Plant));
        Assert.Contains(await Drill("branch", sub), o => o.QualityOrderNo == qoNo);

        var supplierCell = await Drill("supplier", $"{sub}\u0003{supplier}");
        Assert.Contains(supplierCell, o => o.QualityOrderNo == qoNo);
        Assert.All(supplierCell, o => Assert.Equal(supplier, o.Supplier));
        Assert.Contains(await Drill("supplier", $"{sub}\u0003__all"), o => o.QualityOrderNo == qoNo);
        Assert.Contains(await Drill("supplier", supplier), o => o.QualityOrderNo == qoNo);

        Assert.Contains(await Drill("sub", $"{subKey}\u0002{varKey}"), o => o.QualityOrderNo == qoNo);
        Assert.Contains(await Drill("sub", subKey), o => o.QualityOrderNo == qoNo);

        // Two branches chosen: both kept, nothing else.
        var two = d.Plants.Take(2).ToList();
        var multi = await svc.DrillAsync(new DefectDrillRequest { From = From, To = To, Tab = "major", RowKey = "__all", DefectKey = "T", Plant = two }, PlantScope.All);
        Assert.All(multi, o => Assert.Contains(o.Plant, two));
        if (d.Plants.Count >= 2) Assert.Equal(2, multi.Select(o => o.Plant).Distinct().Count());
    }

    [Fact]
    public void Extra_trend_charts_reach_both_export_formats()
    {
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        var r = new DefectReportExportRequest
        {
            Format = "xlsx", Title = "Defects per week", Tab = "Per week", Period = "Received 2026-09-01 to 2026-10-01",
            RowLabel = "Defect",
            Columns = { new DefectReportExportColumn { Label = "W40", Kind = "week" } },
            Rows = { new DefectReportExportRow { Label = "All defects", Level = "gt", Values = { 1.5m } } },
            Charts = { png, png, png }, Generated = "now"
        };
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(DefectReportExporter.Excel(r)));
        Assert.Equal(3, wb.Worksheet("Trend charts").Pictures.Count);
        Assert.True(DefectReportExporter.Pdf(r).Length > 1000);

        r.Charts = Enumerable.Repeat(png, 21).ToList();
        Assert.Throws<ArgumentException>(() => DefectReportExporter.Validate(r));
    }

    // ---- Banana ripeness -------------------------------------------------

    /// <summary>The QC team's sheet counts both days: discharged on the 19th and
    /// pulled out on the 20th is 2 days in port (CGMU5146860).</summary>
    [Theory]
    [InlineData("2026-10-19", "2026-10-20", 2)]
    [InlineData("2026-10-10", "2026-10-19", 10)]
    [InlineData("2026-10-10", "2026-10-10", 1)]
    [InlineData("2026-10-10", "2026-10-09", null)]
    public void Days_in_port_count_both_days(string discharge, string pullOut, int? expected) =>
        Assert.Equal(expected, DefectReportService.BananaDaysInPort(DateTime.Parse(discharge), DateTime.Parse(pullOut)));

    /// <summary>
    /// The four containers on the QC team's sample sheet, against live data:
    /// the counts come from the arrival fields, the percentage is
    /// (yellow + green) ÷ received quantity.
    /// </summary>
    [Fact]
    public async Task Banana_rows_carry_the_arrival_ripeness_counts()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDefectReportService>();
        var rows = await svc.GetBananaRipenessAsync(new DateTime(2026, 10, 20), new DateTime(2026, 10, 31), PlantScope.All);
        var row = rows.FirstOrDefault(r => r.ContainerNo == "CGMU5146860");
        if (row is null) return;   // a database without that container

        Assert.Equal(54m, row.YellowRipe);
        Assert.Equal(165m, row.GreenSoft);
        Assert.True(row.ReceivedQty > 0);
        Assert.Equal(Math.Round(100m * 219 / row.ReceivedQty, 2), row.RipePercent);
        Assert.Equal(2, row.DaysInPort);
        Assert.Equal(row.TransitDays + row.DaysInPort, row.Age);
        Assert.Equal("Ecuador", row.Origin);
        Assert.All(rows, r => Assert.True(r.YellowRipe.HasValue || r.GreenSoft.HasValue));
    }

    [Fact]
    public async Task The_banana_endpoint_answers()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/DefectReports/Banana?from=2026-10-20&to=2026-10-31");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("containerNo", await res.Content.ReadAsStringAsync());
    }

    /// <summary>The Banana tab exports text columns (vessel, container, shipper)
    /// as text, and its own footnote instead of the defect-% one.</summary>
    [Fact]
    public void Text_columns_reach_both_export_formats()
    {
        var r = new DefectReportExportRequest
        {
            Format = "xlsx", Title = "Banana ripeness per container", Tab = "Banana ripeness", Period = "Received 2026-10-20 to 2026-10-31",
            RowLabel = "Vessel Name", Footnote = "Ripe % = (Yellow Ripe + Green Soft) ÷ Received Qty.",
            Columns = { new DefectReportExportColumn { Label = "Container #", Kind = "text" },
                        new DefectReportExportColumn { Label = "Received Qty", Kind = "count" },
                        new DefectReportExportColumn { Label = "Ripe %", Kind = "pct" } },
            Rows = { new DefectReportExportRow { Label = "MSC PANTERA", Values = { null, 1540m, 14.22m }, Texts = { "TTNU8147824", null, null } } },
            Generated = "now"
        };
        using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(DefectReportExporter.Excel(r)));
        var ws = wb.Worksheet(1);
        Assert.Contains(ws.CellsUsed(), c => c.GetString() == "TTNU8147824");
        Assert.Contains(ws.CellsUsed(), c => c.GetString().Contains("Ripe % = (Yellow Ripe"));
        Assert.DoesNotContain(ws.CellsUsed(), c => c.GetString().Contains("defective units"));
        Assert.True(DefectReportExporter.Pdf(r).Length > 1000);
    }
}
