using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
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
    public async Task<IActionResult> Pending(
        string? container, string? bol, string? po,
        string? plant, string? poType, string? storageLoc, string? supplier)
    {
        // Operator plant-scope: if the user is restricted to a plant, force
        // the dropdown value to it (and the view replaces the dropdown with
        // a locked badge). Manager / SiteAdmin / etc. pass null and see all.
        var scoped = User.GetScopedPlant();
        if (scoped != null) plant = scoped;

        var rows    = await _cache.ListPendingAsync(container, bol, po, plant, poType, storageLoc, supplier);
        var status  = await _cache.GetPullStatusAsync();
        var options = await _cache.GetPendingFilterOptionsAsync();
        ViewBag.Container         = container;
        ViewBag.Bol               = bol;
        ViewBag.Po                = po;
        ViewBag.Supplier          = supplier;
        ViewBag.Plant             = plant;
        ViewBag.PoType            = poType;
        ViewBag.StorageLoc        = storageLoc;
        ViewBag.PullStatus        = status;
        ViewBag.PlantOptions      = options.Plants;
        ViewBag.PoTypeOptions     = options.PoTypes;
        ViewBag.StorageLocOptions = options.StorageLocations;
        ViewBag.PlantScopeLocked  = scoped;
        return View(rows);
    }

    /// <summary>Returns Forbid() when the user is plant-scoped and the arrival
    /// belongs to a different plant; null when access is OK.</summary>
    private async Task<IActionResult?> EnsureCanReadArrivalAsync(long arrivalId)
    {
        var scoped = User.GetScopedPlant();
        if (scoped == null) return null;
        var plant = await _arrivals.GetPlantAsync(arrivalId);
        return string.Equals(plant, scoped, StringComparison.OrdinalIgnoreCase)
            ? null
            : Forbid();
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
        var scoped = User.GetScopedPlant();
        var rows = await _arrivals.ListAsync(string.IsNullOrEmpty(status) ? null : status, search, scoped);
        ViewBag.Status = status;
        ViewBag.Search = search;
        ViewBag.PlantScopeLocked = scoped;
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
        // Plant-scope: drop rows that don't belong to the operator's plant.
        var scoped = User.GetScopedPlant();
        if (scoped != null)
            results = results
                .Where(r => string.Equals(r.Plant, scoped, StringComparison.OrdinalIgnoreCase))
                .ToList();
        ViewBag.SapError = sapError;
        ViewBag.PlantScopeLocked = scoped;

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
        // BOL is optional: some containers arrive without a bill of lading. The
        // shipment identity is still (container, BOL, PO) -- a missing BOL is
        // normalised to "" so it matches the empty BOL stored on those SAP rows
        // (qms_sap_container_cache.bol_no is NOT NULL and holds '' for them).
        if (string.IsNullOrWhiteSpace(containerNo) || string.IsNullOrWhiteSpace(po))
        {
            TempData["Error"] = "Container number and PO are required.";
            return RedirectToAction(nameof(Search));
        }
        bolNo = (bolNo ?? "").Trim();

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
                // Treat null/"" BOLs as equal so BOL-less shipments still match.
                string.Equals(r.BolNo ?? "", bolNo,       StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Ebeln,       po,          StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matched.Count == 0)
        {
            TempData["Error"] = $"No SAP rows found for container {containerNo} / BOL {bolNo} / PO {po}.";
            return RedirectToAction(nameof(Search));
        }

        // Plant-scope: refuse to create an arrival outside the operator's plant.
        var scoped = User.GetScopedPlant();
        if (scoped != null &&
            !matched.Any(r => string.Equals(r.Plant, scoped, StringComparison.OrdinalIgnoreCase)))
        {
            TempData["Error"] = $"This shipment belongs to plant {matched.FirstOrDefault()?.Plant ?? "(unknown)"}; you are scoped to {scoped}.";
            return RedirectToAction(nameof(Pending));
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
        if (await EnsureCanReadArrivalAsync(id) is { } block) return block;
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
        // V36: admin-defined fields whose material group appears on this
        // arrival's line items (empty list = the card isn't rendered).
        ViewBag.CustomFields = await _arrivals.GetCustomFieldsAsync(id);
        return View(arrival);
    }


    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveChecklist(ArrivalChecklist checklist)
    {
        if (await EnsureCanReadArrivalAsync(checklist.ArrivalId) is { } block) return block;
        var arrival = await _arrivals.GetAsync(checklist.ArrivalId);
        if (arrival == null) return NotFound();
        // V31 (2026-06-20): Supervisor and above can edit Completed arrivals.
        // Draft is always editable by OperatorOrAbove (the controller-level
        // policy). Cancelled is never editable.
        if (!CanEditArrival(arrival.StatusCode))
        {
            TempData["Error"] = $"Arrival is {arrival.StatusCode} — not editable.";
            return RedirectToAction(nameof(Details), new { id = checklist.ArrivalId });
        }
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        // Seal numbers and record-logger serials are entered as up to 4
        // discrete inputs (SealNos / LoggerSerials). Collapse the non-empty
        // ones, capped at 4, into the single newline-delimited column each is
        // stored in. Overrides whatever the single-value model binding set.
        checklist.SealNo          = JoinMultiInput("SealNos", 4);
        checklist.DataLoggerSerial = JoinMultiInput("LoggerSerials", 4);
        await _arrivals.SaveChecklistAsync(checklist, user);
        // V36.2: the "Additional fields" card lives inside the checklist form
        // (single Save button, per user request) — persist its cf_{fieldId}
        // inputs in the same submit. The service re-derives the applicable
        // field set, so foreign ids are ignored.
        var cfValues = ReadCustomFieldInputs();
        if (cfValues.Count > 0)
            await _arrivals.SaveCustomFieldValuesAsync(checklist.ArrivalId, cfValues, user);
        TempData["Success"] = "Checklist saved.";
        return RedirectToAction(nameof(Details), new { id = checklist.ArrivalId });
    }

    /// <summary>Collapse a repeated form field (e.g. the up-to-4 seal / logger
    /// serial inputs) into a single newline-delimited string: trims each value,
    /// drops blanks and duplicates-by-position, and caps the count. Returns null
    /// when nothing was entered so the column stores NULL rather than "".</summary>
    private string? JoinMultiInput(string name, int max)
    {
        var values = Request.Form[name]
            .Select(v => (v ?? "").Trim())
            .Where(v => v.Length > 0)
            .Take(max)
            .ToList();
        return values.Count == 0 ? null : string.Join("\n", values);
    }

    /// <summary>V36: collect the <c>cf_{fieldId}</c> custom-field inputs from
    /// the posted form.</summary>
    private Dictionary<int, string?> ReadCustomFieldInputs()
    {
        var values = new Dictionary<int, string?>();
        foreach (var key in Request.Form.Keys)
        {
            if (!key.StartsWith("cf_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(key.AsSpan(3), out var fieldId)) continue;
            values[fieldId] = Request.Form[key].ToString();
        }
        return values;
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveShipment(ShipmentSnapshot shipment)
    {
        if (await EnsureCanReadArrivalAsync(shipment.ArrivalId) is { } block) return block;
        var arrival = await _arrivals.GetAsync(shipment.ArrivalId);
        if (arrival == null) return NotFound();
        if (!CanEditArrival(arrival.StatusCode))
        {
            TempData["Error"] = $"Arrival is {arrival.StatusCode} — not editable.";
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
        if (await EnsureCanReadArrivalAsync(id) is { } block) return block;
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
        if (await EnsureCanReadArrivalAsync(id) is { } block) return block;
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
        if (await EnsureCanReadArrivalAsync(id) is { } block) return block;
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

    // V31 (2026-06-20): combined status + role gate for arrival edits.
    // Draft -> any OperatorOrAbove (controller-level policy).
    // Completed -> Supervisor and above only (data fixes after completion).
    // Cancelled -> nobody (even SiteAdmin, who would Reopen first).
    private bool CanEditArrival(string statusCode)
    {
        if (statusCode == ArrivalStatus.Draft) return true;
        if (statusCode == ArrivalStatus.Completed)
        {
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            return role == UserRoles.Supervisor
                || role == UserRoles.Manager
                || role == UserRoles.SiteAdmin;
        }
        return false;
    }
}
