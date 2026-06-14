using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Controllers;

[Authorize]
public class ArrivalsController : Controller
{
    private readonly IArrivalService _arrivals;
    private readonly ISapClient _sap;
    private readonly IMaraService _mara;
    private readonly IContainerCacheService _cache;
    private readonly ILogger<ArrivalsController> _logger;

    public ArrivalsController(IArrivalService arrivals, ISapClient sap, IMaraService mara,
        IContainerCacheService cache, ILogger<ArrivalsController> logger)
    {
        _arrivals = arrivals; _sap = sap; _mara = mara; _cache = cache; _logger = logger;
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Pending(string? container, string? bol, string? po)
    {
        var rows   = await _cache.ListPendingAsync(container, bol, po);
        var status = await _cache.GetPullStatusAsync();
        ViewBag.Container  = container;
        ViewBag.Bol        = bol;
        ViewBag.Po         = po;
        ViewBag.PullStatus = status;
        return View(rows);
    }

    /// <summary>
    /// Operator-friendly twin of AdminController.PullContainersNow. Kicks off
    /// a background SAP pull using the existing admin-saved Container.* settings
    /// and redirects back to the Pending page with a toast. Same in-flight guard
    /// + fire-and-forget pattern as the admin version, so an operator clicking
    /// twice or simultaneously with the auto-scheduler can't double-pull.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> RetrieveLatestContainers(
        [FromServices] ISettingsService settings,
        [FromServices] IConfiguration   config,
        [FromServices] IServiceScopeFactory scopeFactory)
    {
        var cfg = await settings.GetContainerPollConfigAsync();
        if (cfg.StartDate is null)
        {
            TempData["Error"] = "Cannot retrieve: the SAP start date is not configured. Ask an admin to set it under Settings → SAP.";
            return RedirectToAction(nameof(Pending));
        }

        var cs = config.GetConnectionString("Default")!;
        using (var c = new Microsoft.Data.SqlClient.SqlConnection(cs))
        {
            var inFlight = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, @"
                SELECT COUNT(*) FROM qms_sap_sync_log
                WHERE  endpoint_key = @ep AND completed_at IS NULL",
                new { ep = ContainerCacheService.SyncLogEndpointKey });
            if (inFlight > 0)
            {
                TempData["Error"] = "A pull is already running. Refresh the page in a moment to see new containers.";
                return RedirectToAction(nameof(Pending));
            }
        }

        var user      = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var startDate = cfg.StartDate.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var cache = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();
                await cache.RefreshFromSapAsync(startDate, user, "Manual", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background container pull (from Pending page) threw");
            }
        });

        TempData["Success"] = "Retrieving latest containers from SAP in the background. Refresh this page in a moment to see new entries.";
        return RedirectToAction(nameof(Pending));
    }

    public async Task<IActionResult> Index(string? status, string? search)
    {
        var rows = await _arrivals.ListAsync(string.IsNullOrEmpty(status) ? null : status, search);
        ViewBag.Status = status;
        ViewBag.Search = search;
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Search(string? container, string? bol, string? po, string? material)
    {
        var query = new SapSearchQuery
        {
            ContainerNo = container, BolNo = bol, Ebeln = po, MaterialNo = material
        };

        IReadOnlyList<SapShipmentRow> results = Array.Empty<SapShipmentRow>();
        string? sapError = null;
        var anyFilter = !string.IsNullOrWhiteSpace(container) || !string.IsNullOrWhiteSpace(bol)
                        || !string.IsNullOrWhiteSpace(po)     || !string.IsNullOrWhiteSpace(material);
        if (anyFilter)
        {
            try
            {
                results = await _sap.SearchAsync(query);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SAP search failed");
                sapError = ex.Message;
            }
        }
        ViewBag.SapError = sapError;

        var distinctBols = results.Select(r => r.BolNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool ambiguous = !string.IsNullOrWhiteSpace(container)
                         && string.IsNullOrWhiteSpace(bol)
                         && string.IsNullOrWhiteSpace(po)
                         && distinctBols.Count > 1;

        // Pre-flag shipments that already have an arrival so the grid can disable
        // their Create button. An arrival is uniquely (container, BOL, PO), so the
        // same container under a different BOL/PO is still creatable.
        var containers = results.Select(r => r.ContainerNo)
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
        var existing = await _arrivals.FindByContainersAsync(containers);
        var existingByShipment = existing
            .GroupBy(a => ShipmentKey(a.ContainerNo, a.BolNo, a.Ebeln))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.CreatedAt).First());

        ViewBag.Container = container;
        ViewBag.Bol       = bol;
        ViewBag.Po        = po;
        ViewBag.Material  = material;
        ViewBag.Ambiguous = ambiguous;
        ViewBag.BolOptions = distinctBols;
        ViewBag.ExistingByShipment = existingByShipment;
        return View(results);
    }

    // Case-insensitive key identifying one shipment = one arrival
    // (container × BOL × PO). Shared by the search grid (to flag already-created
    // shipments) and the dedup guard below.
    public static string ShipmentKey(string? containerNo, string? bolNo, string? po) =>
        $"{containerNo}|{bolNo}|{po}".ToUpperInvariant();

    // An arrival corresponds to one container shipment line: one container number
    // under one BOL for one PO (EBELN). Every PO *item* line for that triple
    // becomes an arrival item automatically -- the user does not pick lines.
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Create(string containerNo, string bolNo, string po)
    {
        if (string.IsNullOrWhiteSpace(containerNo) || string.IsNullOrWhiteSpace(bolNo) || string.IsNullOrWhiteSpace(po))
        {
            TempData["Error"] = "Container number, BOL, and PO are required.";
            return RedirectToAction(nameof(Search));
        }

        // Block duplicate arrivals for the same (container, BOL, PO). The same
        // container can recur under a different BOL/PO, so all three must match.
        // If the inspector wants to redo it they should open or (admin) delete
        // the existing one.
        var existing = await _arrivals.FindByShipmentAsync(containerNo, bolNo, po);
        if (existing != null)
        {
            // The cookie-based TempData serializer only handles string / int /
            // bool / DateTime / Guid -- Int64 throws. Store ArrivalId as string
            // and re-parse in the view.
            TempData["DuplicateArrivalId"]   = existing.ArrivalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TempData["DuplicateArrivalNo"]   = existing.ArrivalNo;
            TempData["DuplicateContainerNo"] = containerNo;
            TempData["DuplicateBolNo"]       = bolNo;
            TempData["DuplicatePo"]          = po;
            TempData["DuplicateStatus"]      = existing.StatusCode;
            return RedirectToAction(nameof(Search),
                new { container = containerNo, bol = bolNo, po });
        }

        // Cache-first: the polling service has likely already pulled this
        // triplet, so we can build the Arrival without a live SAP round-trip.
        // Falls back to SAP when the cache is empty for the triplet (e.g.
        // entry via /Arrivals/Search for a row SAP just added).
        var matched = (await _cache.GetTripletRowsAsync(containerNo, bolNo, po)).ToList();
        if (matched.Count == 0)
        {
            var rows = await _sap.SearchAsync(new SapSearchQuery
            {
                ContainerNo = containerNo,
                BolNo       = bolNo,
                Ebeln       = po
            });
            matched = rows.Where(r =>
                string.Equals(r.ContainerNo, containerNo, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.BolNo,       bolNo,       StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Ebeln,       po,          StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matched.Count == 0)
        {
            TempData["Error"] = $"No SAP rows found for container {containerNo} / BOL {bolNo} / PO {po}.";
            return RedirectToAction(nameof(Search));
        }

        try
        {
            var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
            var arrivalId = await _arrivals.CreateFromSapAsync(matched, user);
            await _cache.MarkArrivedAsync(containerNo, bolNo, po, arrivalId);
            TempData["Success"] = $"Arrival created from container {containerNo} / BOL {bolNo} / PO {po} ({matched.Count} material line(s)).";
            return RedirectToAction(nameof(Details), new { id = arrivalId });
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Search));
        }
    }

    public async Task<IActionResult> Details(long id)
    {
        var arrival = await _arrivals.GetAsync(id);
        if (arrival == null) return NotFound();

        // Enrich the item lines with Variety / Class from the MARA cache for the
        // Materials tab. qms_arrival_item doesn't snapshot these, so read them live.
        var items = await _arrivals.GetItemsAsync(id);
        var mara = await _mara.LookupAsync(items.Select(i => i.MaterialNo));
        foreach (var it in items)
        {
            if (mara.TryGetValue(it.MaterialNo, out var m))
            {
                it.Variety       = m.Variety;
                it.MaterialClass = m.MaterialClass;
            }
        }

        ViewBag.Items     = items;
        ViewBag.Checklist = await _arrivals.GetChecklistAsync(id) ?? new ArrivalChecklist { ArrivalId = id };
        ViewBag.Shipment  = await _arrivals.GetShipmentAsync(id);
        return View(arrival);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveChecklist(ArrivalChecklist checklist)
    {
        var arrival = await _arrivals.GetAsync(checklist.ArrivalId);
        if (arrival == null) return NotFound();
        if (arrival.StatusCode != ArrivalStatus.Draft)
        {
            TempData["Error"] = "Only Draft arrivals can be edited.";
            return RedirectToAction(nameof(Details), new { id = checklist.ArrivalId });
        }
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _arrivals.SaveChecklistAsync(checklist, user);
        TempData["Success"] = "Checklist saved.";
        return RedirectToAction(nameof(Details), new { id = checklist.ArrivalId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveShipment(ShipmentSnapshot shipment)
    {
        var arrival = await _arrivals.GetAsync(shipment.ArrivalId);
        if (arrival == null) return NotFound();
        if (arrival.StatusCode != ArrivalStatus.Draft)
        {
            TempData["Error"] = "Only Draft arrivals can be edited.";
            return RedirectToAction(nameof(Details), new { id = shipment.ArrivalId });
        }
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _arrivals.SaveShipmentAsync(shipment, user);
        TempData["Success"] = "Shipment details saved.";
        return RedirectToAction(nameof(Details), new { id = shipment.ArrivalId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Complete(long id)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (ok, error) = await _arrivals.CompleteAsync(id, user);
        TempData[ok ? "Success" : "Error"] = ok
            ? "Arrival completed. You can now open a Quality Order."
            : error;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> ReopenForEdit(long id, string? reason)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (ok, error) = await _arrivals.ReopenForEditAsync(id, user, reason);
        TempData[ok ? "Success" : "Error"] = ok
            ? "Arrival is now Draft and editable. Save your changes and Complete it again when done."
            : error;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Delete(long id)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (ok, error) = await _arrivals.DeleteAsync(id, user);
        if (!ok)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }
        TempData["Success"] = "Arrival deleted.";
        return RedirectToAction(nameof(Index));
    }
}
