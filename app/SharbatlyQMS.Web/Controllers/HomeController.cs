using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

public class HomeController : Controller
{
    private readonly IDashboardService _dashboard;
    private readonly IUserPermissions _perms;
    private readonly ILogger<HomeController> _logger;

    public HomeController(IDashboardService dashboard, IUserPermissions perms,
        ILogger<HomeController> logger)
    {
        _dashboard = dashboard;
        _perms     = perms;
        _logger    = logger;
    }

    [RequireScreen(Screens.Dashboard, Seed.Everyone, "Open the dashboard")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = await _dashboard.GetSummaryAsync(ct);
        // Was User.IsInRole(SiteAdmin). The dashboard's admin panel is really
        // "may this person administer settings", which is now a permission a
        // composed role can hold without being the built-in administrator.
        vm.IsAdmin = _perms.CanView(Screens.AdminSettings);
        return View(vm);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
    });

    // Friendly handler for non-success status codes, wired via
    // app.UseStatusCodePagesWithReExecute("/Home/HttpError", "?code={0}").
    // Preserves the original status code on the response.
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult HttpError(int? code)
    {
        Response.StatusCode = code ?? 500;
        return View("HttpError", code ?? 500);
    }
}
