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
