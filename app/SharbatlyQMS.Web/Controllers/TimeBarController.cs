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
}
