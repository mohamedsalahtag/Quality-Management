using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Reports -> Defect reports. The five defect pivots the QC team kept in Excel,
/// as tabs on one page, every number clickable down to the orders behind it.
/// The pivoting happens in the browser (wwwroot/js/defect-reports.js) over the
/// dataset <see cref="Data"/> returns; see <see cref="DefectReportService"/> for
/// what the two figures mean.
/// </summary>
[Authorize]
public class DefectReportsController : Controller
{
    private readonly IDefectReportService _reports;
    private readonly ILogger<DefectReportsController> _log;

    public DefectReportsController(IDefectReportService reports, ILogger<DefectReportsController> log)
    {
        _reports = reports;
        _log = log;
    }

    /// <summary>Opens on the last eight weeks (eight Saturday-to-Friday weeks
    /// ending this week), which covers a typical review without a slow load.</summary>
    public static (DateTime From, DateTime To) DefaultWindow()
    {
        var to = DateTime.Today;
        return (DefectReportService.WeekStart(to).AddDays(-7 * 7), to);
    }

    [HttpGet]
    [RequireScreen(Screens.ReportsDefects, Seed.SupervisorOrAbove, "Open the Defect reports")]
    public IActionResult Index()
    {
        var (from, to) = DefaultWindow();
        ViewBag.From = from.ToString("yyyy-MM-dd");
        ViewBag.To   = to.ToString("yyyy-MM-dd");
        ViewBag.MaxDays = DefectReportService.MaxDays;
        return View();
    }

    [HttpGet]
    [RequireScreen(Screens.ReportsDefects, Seed.SupervisorOrAbove, "Open the Defect reports")]
    public async Task<IActionResult> Data(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var (df, dt) = DefaultWindow();
        var data = await _reports.GetDatasetAsync(from ?? df, to ?? dt, User.GetPlantScope(), ct);
        return Json(data);
    }

    /// <summary>The orders behind one number, as a workbook.</summary>
    [HttpGet]
    [RequireScreen(Screens.ReportsDefects, Seed.SupervisorOrAbove, "Open the Defect reports")]
    public async Task<IActionResult> DrillExcel([FromQuery] DefectDrillRequest req, string? title, CancellationToken ct)
    {
        var rows = await _reports.DrillAsync(req, User.GetPlantScope(), ct);
        var excel = string.Equals(req.Metric, "xls", StringComparison.OrdinalIgnoreCase);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Orders");
        ws.Cell(1, 1).Value = string.IsNullOrWhiteSpace(title) ? "Defect report drill-down" : title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Value = $"Received {req.From:yyyy-MM-dd} to {req.To:yyyy-MM-dd}. " +
                              (excel ? "Excel average: the plain average of each sample's Defect %."
                                     : "QC report %: defective units ÷ inspected units.");
        ws.Cell(2, 1).Style.Font.Italic = true;

        var headers = new[] { "QC order", "Received", "Branch", "Supplier", "Container", "Material",
                              "Samples", "Inspected units", "Defective units", excel ? "Excel average %" : "QC report %" };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(4, i + 1).Value = headers[i];
            ws.Cell(4, i + 1).Style.Font.Bold = true;
        }
        var r = 5;
        foreach (var o in rows)
        {
            ws.Cell(r, 1).Value = o.QualityOrderNo;
            ws.Cell(r, 2).Value = o.ReceiveDate;
            ws.Cell(r, 2).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 3).Value = o.Plant;
            ws.Cell(r, 4).Value = o.Supplier;
            ws.Cell(r, 5).Value = o.ContainerNo;
            ws.Cell(r, 6).Value = string.Join(", ", o.Materials);
            ws.Cell(r, 7).Value = o.Samples;
            ws.Cell(r, 8).Value = (double)o.Inspected;
            ws.Cell(r, 9).Value = (double)o.Defective;
            if (o.Percent.HasValue)
            {
                ws.Cell(r, 10).Value = (double)Math.Round(o.Percent.Value, 2);
                ws.Cell(r, 10).Style.NumberFormat.Format = "0.00";
            }
            r++;
        }
        ws.Range(4, 1, 4, headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(4);
        ws.Columns(1, headers.Length).AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        _log.LogInformation("Defect report drill export: tab {Tab}, row {Row}, defect {Defect}, {Count} orders",
            req.Tab, req.RowKey, req.DefectKey, rows.Count);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"defect-report-orders-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }
}
