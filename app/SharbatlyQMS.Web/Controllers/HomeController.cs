using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using SharbatlyQMS.Web.Extensions;

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
    public async Task<IActionResult> Index([FromQuery] DashboardFilter filter, CancellationToken ct)
    {
        // The user's plant entitlement bounds everything; the filter can only
        // narrow it further (the service drops an out-of-scope plant rather
        // than refusing, so a shared link degrades instead of 403-ing).
        var vm = await _dashboard.GetSummaryAsync(filter ?? new DashboardFilter(),
                                                 User.GetPlantScope(), ct);
        // Was User.IsInRole(SiteAdmin). The dashboard's admin panel is really
        // "may this person administer settings", which is now a permission a
        // composed role can hold without being the built-in administrator.
        vm.IsAdmin = _perms.CanView(Screens.AdminSettings);
        return View(vm);
    }

    /// <summary>
    /// The Received vs Inspected table as a workbook, for the same period and
    /// plant scope the dashboard is showing. Small by nature -- one row per
    /// plant -- so it is built in memory rather than streamed.
    /// </summary>
    [RequireScreen(Screens.Dashboard, Seed.Everyone, "Open the dashboard")]
    public async Task<IActionResult> CommitmentExcel([FromQuery] DashboardFilter filter, CancellationToken ct)
    {
        var vm = await _dashboard.GetSummaryAsync(filter ?? new DashboardFilter(),
                                                 User.GetPlantScope(), ct);

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Received vs Inspected");

        var headers = new[]
        {
            "Plant", "Received", "Period Inspection", "Pending Inspection",
            "Coverage %", "Backlog Inspected", "Total Inspected", "Daily Average"
        };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var row in vm.Commitment.OrderByDescending(x => x.Received).ThenBy(x => x.Plant))
        {
            ws.Cell(r, 1).Value = row.Plant;
            ws.Cell(r, 2).Value = row.Received;
            ws.Cell(r, 3).Value = row.Committed;
            ws.Cell(r, 4).Value = row.Outstanding;
            // Blank rather than 0 when nothing arrived: a coverage of zero
            // percent and "no containers to cover" are different statements.
            if (row.CoveragePct.HasValue) ws.Cell(r, 5).Value = Math.Round(row.CoveragePct.Value, 1);
            ws.Cell(r, 6).Value = row.QosCatchUp;
            ws.Cell(r, 7).Value = row.QosCreated;
            ws.Cell(r, 8).Value = Math.Round(row.DailyAverage(vm.PeriodDays) ?? 0, 2);
            r++;
        }

        if (vm.Commitment.Count > 0)
        {
            var t = r;
            ws.Cell(t, 1).Value = "All plants";
            ws.Cell(t, 2).Value = vm.Commitment.Sum(x => x.Received);
            ws.Cell(t, 3).Value = vm.Commitment.Sum(x => x.Committed);
            ws.Cell(t, 4).Value = vm.Commitment.Sum(x => x.Outstanding);
            var recv = vm.Commitment.Sum(x => x.Received);
            // Recomputed from the totals, not averaged across rows: averaging
            // percentages weights a two-container plant like a forty-container
            // one.
            if (recv > 0) ws.Cell(t, 5).Value = Math.Round(100.0 * vm.Commitment.Sum(x => x.Committed) / recv, 1);
            ws.Cell(t, 6).Value = vm.Commitment.Sum(x => x.QosCatchUp);
            ws.Cell(t, 7).Value = vm.Commitment.Sum(x => x.QosCreated);
            ws.Cell(t, 8).Value = Math.Round((double)vm.Commitment.Sum(x => x.QosCreated) / Math.Max(1, vm.PeriodDays), 2);
            ws.Row(t).Style.Font.Bold = true;
        }

        // The period the numbers describe, so a saved file still says what it
        // is a month later.
        var (pf, pt) = (filter ?? new DashboardFilter()).Resolve();
        ws.Cell(r + 2, 1).Value = $"Period {pf:yyyy-MM-dd} to {pt:yyyy-MM-dd} ({vm.PeriodDays} day(s))";
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"received-vs-inspected-{pf:yyyyMMdd}-{pt:yyyyMMdd}.xlsx");
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
