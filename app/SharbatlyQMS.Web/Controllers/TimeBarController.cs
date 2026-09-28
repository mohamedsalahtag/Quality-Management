using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// The inspection clock for every container SAP sent.
///
/// Exists because the claim window is short: if a quality order shows the fruit
/// is not good, the claim can only be raised inside a specific period from
/// arrival. So the question this page answers is not "how did the inspections
/// go" but "did every container get one, and how long did it take" — including
/// the containers nobody has created an arrival for at all, whose clocks are
/// running with no one watching.
/// </summary>
[Authorize]
public class TimeBarController : Controller
{
    private readonly ITimeBarService _timeBar;
    private readonly ISettingsService _settings;

    public TimeBarController(ITimeBarService timeBar, ISettingsService settings)
    {
        _timeBar  = timeBar;
        _settings = settings;
    }

    [HttpGet]
    [RequireScreen(Screens.TimeBar, Seed.AdminOnly, "Open the Time Bar page")]
    public async Task<IActionResult> Index([FromQuery] TimeBarFilter filter, CancellationToken ct)
    {
        filter ??= new TimeBarFilter();

        // Plant scope bounds everything, exactly as it does on every other list:
        // a plant-restricted user sees their own containers' clocks and no one
        // else's.
        var scope   = User.GetPlantScope();
        var cfg     = await _settings.GetTimeBarConfigAsync();
        var result  = await _timeBar.ListAsync(filter, scope, cfg, ct);
        var options = await _timeBar.GetFilterOptionsAsync(scope, ct);

        ViewBag.Filter           = filter;
        ViewBag.Config           = cfg;
        ViewBag.PlantOptions     = options.Plants;
        ViewBag.PoTypeOptions    = options.PoTypes;
        ViewBag.StatusOptions    = options.Statuses;
        ViewBag.PlantScopeLocked = User.SinglePlantOrNull();
        ViewBag.Total            = result.Total;
        ViewBag.Page             = result.Page;
        ViewBag.PageSize         = result.PageSize;
        ViewBag.Summary          = result.Summary;
        // The clock on a running row was computed at render time; say when that
        // was rather than letting a page left open all afternoon look current.
        ViewBag.AsOf             = DateTime.Now;
        return View(result.Rows);
    }

    /// <summary>
    /// The whole filtered list as a workbook -- every page, not the one on
    /// screen, because an export of page 1 of 18 is a trap.
    ///
    /// Paged through rather than fetched in one query: the list method clamps
    /// its page size deliberately, and this respects that instead of opening a
    /// second, unbounded path into the same data. Capped so a filter that
    /// matches everything cannot build a workbook nobody can open.
    /// </summary>
    [RequireScreen(Screens.TimeBar, Seed.AdminOnly, "Open the Time Bar page")]
    public async Task<IActionResult> Excel([FromQuery] TimeBarFilter filter, CancellationToken ct)
    {
        const int MaxRows = 20000;
        filter ??= new TimeBarFilter();
        filter.PageSize = 100;

        var scope = User.GetPlantScope();
        var cfg   = await _settings.GetTimeBarConfigAsync();

        var rows = new List<TimeBarRow>();
        for (var page = 1; rows.Count < MaxRows; page++)
        {
            filter.Page = page;
            var result = await _timeBar.ListAsync(filter, scope, cfg, ct);
            if (result.Rows.Count == 0) break;
            rows.AddRange(result.Rows);
            if (rows.Count >= result.Total) break;
        }

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Inspection Time Bar");

        var clockStart = cfg.ArrivalBasis == TimeBarArrivalBases.PortArrival ? "Vessel arrival date" : "Receive date";
        var headers = new[]
        {
            "Container", "BOL", "PO", "STO", "Supplier", "Plant", "PO type",
            clockStart, "Date source", "Arrival no", "Quality order",
            "Stage", "Status", "Inspection time bar (days)", "Still running", "QC finished",
            "Not in SAP cache", "Archived", "Inspection date"
        };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var row in rows)
        {
            ws.Cell(r,  1).Value = row.ContainerNo;
            ws.Cell(r,  2).Value = row.BolNo;
            ws.Cell(r,  3).Value = row.Ebeln;
            ws.Cell(r,  4).Value = row.Sto;
            ws.Cell(r,  5).Value = row.VendorName;
            ws.Cell(r,  6).Value = row.Plant;
            ws.Cell(r,  7).Value = row.PoType;
            if (row.ArrivalDate.HasValue) ws.Cell(r, 8).Value = row.ArrivalDate.Value;
            ws.Cell(r,  9).Value = row.ArrivalSource;
            ws.Cell(r, 10).Value = row.ArrivalNo;
            ws.Cell(r, 11).Value = row.QualityOrderNo;
            ws.Cell(r, 12).Value = row.Stage;
            ws.Cell(r, 13).Value = row.StatusCode;
            // Left EMPTY, not zero, when there is no clock: a container with no
            // arrival date has an unknown elapsed time, and a zero would be
            // averaged and charted as if it were instant.
            if (row.ElapsedDays.HasValue) ws.Cell(r, 14).Value = row.ElapsedDays.Value;
            ws.Cell(r, 15).Value = row.IsRunning ? "Yes" : "No";
            if (row.ClosedAt.HasValue) ws.Cell(r, 16).Value = ShipmentDates.ToLocal(row.ClosedAt.Value);
            ws.Cell(r, 17).Value = row.NotInCache ? "Yes" : "No";
            ws.Cell(r, 18).Value = row.IsArchived ? "Yes" : "No";
            // Appended last so a workbook someone already built formulas on
            // keeps its columns where they were.
            if (row.InspectedAt.HasValue) ws.Cell(r, 19).Value = ShipmentDates.ToLocal(row.InspectedAt.Value).Date;
            r++;
        }

        var measure = $"Inspection time bar = inspection date (Quality Order opened) - {clockStart.ToLowerInvariant()}.";
        ws.Cell(r + 1, 1).Value = cfg.StartDate.HasValue
            ? $"{measure} Containers received on or after {cfg.StartDate:yyyy-MM-dd}. Green up to {cfg.GoodDays} day(s), red above {cfg.WarnDays}."
            : $"{measure} All containers. Green up to {cfg.GoodDays} day(s), red above {cfg.WarnDays}.";
        if (rows.Count >= MaxRows)
            ws.Cell(r + 2, 1).Value = $"Truncated at {MaxRows:N0} rows - narrow the filter for the rest.";
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"inspection-time-bar-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    /// <summary>
    /// Sets the page's own start date without a trip to Site Configuration.
    ///
    /// It lives here because this is where the question is asked. The date was
    /// only editable on the Alerts tab of the settings screen, three clicks and
    /// one tab away from the page it governs, and it was not found.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SetStartDate(DateOnly? startDate, CancellationToken ct = default)
    {
        var cfg = await _settings.GetTimeBarConfigAsync();
        cfg.StartDate = startDate;
        await _settings.SaveTimeBarConfigAsync(cfg, GetCurrentUserId());
        TempData["Success"] = startDate.HasValue
            ? $"Showing containers that arrived on or after {startDate:yyyy-MM-dd}."
            : "Showing every container, whenever it arrived.";
        return RedirectToAction(nameof(Index));
    }

    // Same claim the rest of the application reads for the acting user.
    private int GetCurrentUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return int.TryParse(claim?.Value, out var id) ? id : 0;
    }
}
