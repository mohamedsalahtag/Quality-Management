using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

public class HomeController : Controller
{
    private readonly IDashboardService _dashboard;
    private readonly ILogger<HomeController> _logger;

    public HomeController(IDashboardService dashboard, ILogger<HomeController> logger)
    {
        _dashboard = dashboard;
        _logger    = logger;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = await _dashboard.GetSummaryAsync(ct);
        vm.IsAdmin = User.IsInRole(UserRoles.SiteAdmin);
        return View(vm);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
    });
}
