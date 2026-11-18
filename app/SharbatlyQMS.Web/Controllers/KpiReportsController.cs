using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Reports -> KPI reports: performance per user in four areas (volume, speed,
/// quality of work, claims and reporting), compared with the team. Read-only;
/// QC Manager and Admin by default, plant-scoped like every report.
/// </summary>
[Authorize]
public class KpiReportsController : Controller
{
    private readonly IKpiReportService _kpi;

    public KpiReportsController(IKpiReportService kpi) => _kpi = kpi;

    /// <summary>The last four weeks, today included.</summary>
    public static (DateTime From, DateTime To) DefaultWindow() => (DateTime.Today.AddDays(-27), DateTime.Today);

    [HttpGet]
    [RequireScreen(Screens.ReportsKpi, Seed.ManagerOrAdmin, "Open KPI reports")]
    public IActionResult Index()
    {
        var (from, to) = DefaultWindow();
        ViewBag.From = from.ToString("yyyy-MM-dd");
        ViewBag.To = to.ToString("yyyy-MM-dd");
        ViewBag.MaxDays = KpiReportService.MaxDays;
        return View();
    }

    [HttpGet]
    [RequireScreen(Screens.ReportsKpi, Seed.ManagerOrAdmin, "Open KPI reports")]
    public async Task<IActionResult> Data(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var (df, dt) = DefaultWindow();
        return Json(await _kpi.GetAsync(from ?? df, to ?? dt, User.GetPlantScope(), ct));
    }

    /// <summary>The orders or arrivals behind one user's figure.</summary>
    [HttpGet]
    [RequireScreen(Screens.ReportsKpi, Seed.ManagerOrAdmin, "Open KPI reports")]
    public async Task<IActionResult> Drill(string kpi, string user, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var (df, dt) = DefaultWindow();
        return Json(await _kpi.DrillAsync(kpi ?? "", user ?? "", from ?? df, to ?? dt, User.GetPlantScope(), ct));
    }

    /// <summary>The tab on screen as Excel or PDF, through the Defect reports exporter.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [RequireScreen(Screens.ReportsKpi, Seed.ManagerOrAdmin, "Open KPI reports")]
    public IActionResult Export([FromBody] DefectReportExportRequest req)
    {
        if (req is null) return BadRequest();
        req.TitlePrefix = "KPI reports";
        try { DefectReportExporter.Validate(req); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        if (string.Equals(req.Format, "pdf", StringComparison.OrdinalIgnoreCase))
            return File(DefectReportExporter.Pdf(req), "application/pdf", $"kpi-report-{stamp}.pdf");
        return File(DefectReportExporter.Excel(req),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"kpi-report-{stamp}.xlsx");
    }
}
