using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Sap;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

[Authorize]
public class ArrivalsController : Controller
{
    private readonly IArrivalService _arrivals;
    private readonly ISapClient _sap;
    private readonly IMaraService _mara;
    private readonly IContainerCacheService _cache;
    private readonly IUserPermissions _me;
    private readonly ILogger<ArrivalsController> _logger;

    public ArrivalsController(IArrivalService arrivals, ISapClient sap, IMaraService mara,
        IContainerCacheService cache, IUserPermissions me, ILogger<ArrivalsController> logger)
    {
        _arrivals = arrivals; _sap = sap; _mara = mara; _cache = cache; _me = me; _logger = logger;
    }

    [HttpGet]
    [RequireScreen(Screens.ArrivalsPending, Seed.OperatorOrAbove, "Open Pending Containers")]
    public async Task<IActionResult> Pending(
        string? container, string? bol, string? po,
        string? plant, string? poType, string? storageLoc, string? supplier,
        string? material, DateOnly? from, DateOnly? to,
        DateOnly? arrFrom = null, DateOnly? arrTo = null,
        int page = 1, int pageSize = 100, bool archived = false)
    {
        // Server-side paging: the pending cache holds thousands of triplets, so
        // rendering them all in one payload was the page's slowness. Only one
        // page of rows (and only their material lines) is fetched. pageSize is
        // clamped to the two the UI offers.
        if (pageSize != 50 && pageSize != 100) pageSize = 100;

        // Operator plant-scope: if the user is restricted to a plant, force
        // the dropdown value to it (and the view replaces the dropdown with
        // a locked badge). Manager / SiteAdmin / etc. pass null and see all.
        // archived=true swaps the whole page over to the archive: same filters,
        // same pager, only containers that were filed away -- each with a
        // Restore action instead of Create.
        var scope   = User.GetPlantScope();
        var result  = await _cache.ListPendingAsync(container, bol, po, plant, poType, storageLoc, supplier, material, from, to, arrFrom, arrTo, page, pageSize, scope, archived);
        var status  = await _cache.GetPullStatusAsync();
        var options = await _cache.GetPendingFilterOptionsAsync(scope, archived);
        ViewBag.Container         = container;
        ViewBag.Bol               = bol;
        ViewBag.Po                = po;
        ViewBag.Supplier          = supplier;
        ViewBag.Material          = material;
        ViewBag.From              = from;
        ViewBag.To                = to;
        ViewBag.ArrFrom           = arrFrom;
        ViewBag.ArrTo             = arrTo;
        ViewBag.Plant             = plant;
        ViewBag.PoType            = poType;
        ViewBag.StorageLoc        = storageLoc;
        ViewBag.PullStatus        = status;
        ViewBag.PlantOptions      = options.Plants;
        ViewBag.PoTypeOptions     = options.PoTypes;
        ViewBag.StorageLocOptions = options.StorageLocations;
        ViewBag.PlantScopeLocked  = User.SinglePlantOrNull();
        ViewBag.Page              = result.Page;
        ViewBag.PageSize          = result.PageSize;
        ViewBag.TotalCount        = result.Total;
        ViewBag.Archived          = archived;
        ViewBag.ArchivedCount     = await _cache.CountArchivedTripletsAsync(scope);
        return View(result.Rows);
    }

    /// <summary>
    /// Files stale pending containers away by arrival-date range. They stop showing
    /// in Pending Containers (and in the dashboard's pending count) but are not
    /// deleted -- the next SAP sweep would only re-insert them, and the archive
    /// view can put any of them back. The range matches the "PO" date the grid
    /// prints. A plant-scoped manager only ever archives their own plants'
    /// containers; the service applies the same scope the list does.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.Archive, Seed.ManagerOrAdmin, "Archive / restore pending containers")]
    public async Task<IActionResult> ArchivePending(DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            TempData["Error"] = "Give at least one date bound — archiving the whole list at once is never what you want.";
            return RedirectToAction(nameof(Pending));
        }
        if (from is not null && to is not null && from > to)
        {
            TempData["Error"] = "The 'from' date is after the 'to' date.";
            return RedirectToAction(nameof(Pending));
        }

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var n    = await _cache.ArchiveByArrivalDateRangeAsync(from, to, user, User.GetPlantScope());
        _logger.LogInformation("{User} archived {Count} pending containers (arrival date {From}..{To})",
            user, n, from, to);

        TempData[n == 0 ? "Error" : "Success"] = n == 0
            ? "No pending containers fall in that arrival-date range — nothing was archived."
            : $"Archived {n} container{(n == 1 ? "" : "s")}. They are out of the pending list; open Archived to restore any of them.";
        return RedirectToAction(nameof(Pending));
    }

    /// <summary>
    /// Puts archived containers back in the pending list — the exact inverse of
    /// <see cref="ArchivePending"/>, by the same arrival-date range, so an archive
    /// that reached too far is undone in one action rather than row by row.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.Archive, Seed.ManagerOrAdmin, "Archive / restore pending containers")]
    public async Task<IActionResult> RestorePendingRange(DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            TempData["Error"] = "Give at least one date bound to restore a range.";
            return RedirectToAction(nameof(Pending), new { archived = true });
        }
        if (from is not null && to is not null && from > to)
        {
            TempData["Error"] = "The 'from' date is after the 'to' date.";
            return RedirectToAction(nameof(Pending), new { archived = true });
        }

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var n    = await _cache.RestoreByArrivalDateRangeAsync(from, to, user, User.GetPlantScope());
        _logger.LogInformation("{User} restored {Count} archived containers (arrival date {From}..{To})",
            user, n, from, to);

        TempData[n == 0 ? "Error" : "Success"] = n == 0
            ? "No archived containers fall in that arrival-date range — nothing was restored."
            : $"Restored {n} container{(n == 1 ? "" : "s")} to the pending list.";
        return RedirectToAction(nameof(Pending), new { archived = true });
    }

    /// <summary>
    /// Restores one archived container back into the pending list. Stays on the
    /// archive view so the operator can keep working down the page.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.Archive, Seed.ManagerOrAdmin, "Archive / restore pending containers")]
    public async Task<IActionResult> RestorePending(string containerNo, string bolNo, string po)
    {
        if (string.IsNullOrWhiteSpace(containerNo) || string.IsNullOrWhiteSpace(po))
        {
            TempData["Error"] = "Container and PO are required.";
            return RedirectToAction(nameof(Pending), new { archived = true });
        }
        bolNo = (bolNo ?? "").Trim();

        // Same scope gate as the plant override: the container has to be one
        // this user is allowed to see before they can move it anywhere.
        var scope = User.GetPlantScope();
        if (!scope.Unrestricted)
        {
            var plant = await _cache.GetEffectivePlantAsync(containerNo.Trim(), bolNo, po.Trim());
            if (plant == null || !scope.Allows(plant)) return Forbid();
        }

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var n    = await _cache.SetArchivedAsync(containerNo.Trim(), bolNo, po.Trim(), archived: false, user);
        TempData[n == 0 ? "Error" : "Success"] = n == 0
            ? "Nothing was changed — that container is no longer in the archive."
            : $"Container {containerNo} is back in the pending list.";
        return RedirectToAction(nameof(Pending), new { archived = true });
    }

    /// <summary>Returns Forbid() when the user is plant-scoped and the arrival
    /// belongs to a different plant; null when access is OK.</summary>
    private async Task<IActionResult?> EnsureCanReadArrivalAsync(long arrivalId)
    {
        var scope = User.GetPlantScope();
        if (scope.Unrestricted) return null;
        var plant = await _arrivals.GetPlantAsync(arrivalId);
        return scope.Allows(plant) ? null : Forbid();
    }

    /// <summary>
    /// Operator-friendly twin of AdminController.PullContainersNow. Kicks off
    /// a background SAP pull using the existing admin-saved Container.* settings
    /// and redirects back to the Pending page with a toast. Same in-flight guard
    /// + fire-and-forget pattern as the admin version, so an operator clicking
    /// twice or simultaneously with the auto-scheduler can't double-pull.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.Retrieve, Seed.OperatorOrAbove, "Retrieve latest containers from SAP")]
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

    /// <summary>
    /// QC Manager / Admin: reassign a pending SAP container to a different plant.
    /// The container moves into the target plant's Pending list (visible to that
    /// plant's users) and out of the original's; the Arrival + Quality Order it
    /// later becomes are created under the target plant. Passing the container's
    /// own SAP plant clears a previous override. SAP data is left untouched.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.OverridePlant, Seed.ManagerOrAdmin, "Override a pending container's plant")]
    public async Task<IActionResult> OverridePlant(
        string containerNo, string bolNo, string po, string plant,
        [FromServices] ICodeDescriptionDirectory codes)
    {
        if (string.IsNullOrWhiteSpace(containerNo) || string.IsNullOrWhiteSpace(po) || string.IsNullOrWhiteSpace(plant))
        {
            TempData["Error"] = "Container, PO and target plant are required.";
            return RedirectToAction(nameof(Pending));
        }
        bolNo = (bolNo ?? "").Trim();
        plant = plant.Trim();

        // Target must be a real plant code.
        if (!codes.Plants.Any(p => string.Equals(p.Code, plant, StringComparison.OrdinalIgnoreCase)))
        {
            TempData["Error"] = $"Unknown plant '{plant}'.";
            return RedirectToAction(nameof(Pending));
        }

        var scope = User.GetPlantScope();
        // The container must currently be one the user can see, and a plant-scoped
        // manager can only reassign into a plant they are allowed. Unrestricted
        // managers/admins pass both.
        var currentPlant = await _cache.GetEffectivePlantAsync(containerNo, bolNo, po);
        if (currentPlant == null)
        {
            TempData["Error"] = "That container is no longer pending (it may already have an arrival).";
            return RedirectToAction(nameof(Pending));
        }
        if (!scope.Unrestricted && (!scope.Allows(currentPlant) || !scope.Allows(plant)))
            return Forbid();

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var affected = await _cache.SetPlantOverrideAsync(containerNo, bolNo, po, plant, user);
        if (affected == 0)
        {
            TempData["Error"] = "Nothing was changed — the container may no longer be pending.";
        }
        else
        {
            TempData["Success"] = string.Equals(plant, currentPlant, StringComparison.OrdinalIgnoreCase)
                ? $"Container {containerNo} is already on plant {codes.PlantDisplay(plant)}."
                : $"Container {containerNo} moved to plant {codes.PlantDisplay(plant)}. It is now visible to that plant's users; create the Quality Order from there.";
        }
        return RedirectToAction(nameof(Pending));
    }

    [RequireScreen(Screens.ArrivalsIndex, Seed.Everyone, "Open Arrivals")]
    public async Task<IActionResult> Index([FromQuery] ArrivalListFilter filter)
    {
        // Plant-scoped operators can't widen their view: the scope overrides
        // whatever the panel's plant dropdown posted (the view renders a locked
        // badge + hidden input to match). Same pattern as Quality Orders.
        var scope   = User.GetPlantScope();
        var result  = await _arrivals.ListAsync(filter, scope);
        var options = await _arrivals.GetArrivalFilterOptionsAsync(scope);
        ViewBag.Filter           = filter;
        ViewBag.FilterOptions    = options;
        ViewBag.PlantScopeLocked = User.SinglePlantOrNull();
        // Page window for the server-side pager. The view keeps taking the row
        // list as its model so only the pager markup had to change.
        ViewBag.Total    = result.Total;
        ViewBag.Page     = result.Page;
        ViewBag.PageSize = result.PageSize;
        return View(result.Rows);
    }

    [HttpGet]
    [RequireScreen(Screens.ArrivalsSearch, Seed.OperatorOrAbove, "Search SAP for a container")]
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
        // Plant-scope: drop rows that don't belong to the user's assigned plants.
        var scope = User.GetPlantScope();
        if (!scope.Unrestricted)
            results = results.Where(r => scope.Allows(r.Plant)).ToList();
        ViewBag.SapError = sapError;
        ViewBag.PlantScopeLocked = User.SinglePlantOrNull();

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
    [RequirePermission(Perm.Arrivals.Create, Seed.OperatorOrAbove, "Create an arrival")]
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

        // A manager may have reassigned this container to another plant; the
        // arrival (and the QO that inherits its plant) must land there. The
        // effective plant is the override when present, else the SAP row's
        // plant. Fallback (live-SAP, not in the pending cache) has no override.
        var overridePlant  = await _cache.GetPlantOverrideAsync(containerNo, bolNo, po);
        var effectivePlant = overridePlant ?? matched.FirstOrDefault()?.Plant;

        // Plant-scope: refuse to create an arrival outside the user's plants.
        var scope = User.GetPlantScope();
        if (!scope.Unrestricted && !scope.Allows(effectivePlant))
        {
            var yours = scope.Plants.Count == 0 ? "(none)" : string.Join(", ", scope.Plants);
            TempData["Error"] = $"This shipment belongs to plant {effectivePlant ?? "(unknown)"}; you are limited to {yours}.";
            return RedirectToAction(nameof(Pending));
        }

        try
        {
            var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
            var arrivalId = await _arrivals.CreateFromSapAsync(matched, user, overridePlant);
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

    [RequireScreen(Screens.ArrivalsDetails, Seed.Everyone, "Open an arrival")]
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
        // Per-field rules (W6): which editable fields are still editable given
        // the arrival status, and which are mandatory (shown with a * marker).
        var policies = await _arrivals.GetArrivalFieldPoliciesAsync();
        var editable = await FieldEditableAsync(arrival.StatusCode);
        ViewBag.FieldEditable = editable;
        ViewBag.MandatoryKeys = new HashSet<string>(
            policies.Where(kv => kv.Value.IsMandatory).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);
        return View(arrival);
    }

    /// <summary>Per-field editability for an arrival's forms. Draft: everything
    /// editable. Completed: administrators still edit everything (unchanged), and
    /// any field flagged "editable when closed" on the Arrival Field Rules page
    /// stays editable for everyone (e.g. Joint Survey). Cancelled: nothing.</summary>
    private async Task<Func<string, bool>> FieldEditableAsync(string status)
    {
        if (status == ArrivalStatus.Draft) return _ => true;
        // Rejected behaves like Completed here: the inspection is over, but the
        // shipment dates the claim report prints must still be correctable.
        if (status != ArrivalStatus.Completed && status != ArrivalStatus.Rejected) return _ => false;
        // Was IsInRole(QcAdmin): a role name compiled into the controller, so no
        // administrator could see it on the Security screen or move it to
        // another role. It is a permission now, seeded to the same audience.
        bool canOverride = _me.Can(Perm.Arrivals.EditClosedFields);
        var policies = await _arrivals.GetArrivalFieldPoliciesAsync();
        return key => canOverride || (policies.TryGetValue(key, out var p) && p.EditableWhenClosed);
    }


    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.SaveChecklist, Seed.OperatorOrAbove, "Save the arrival checklist")]
    public async Task<IActionResult> SaveChecklist(ArrivalChecklist checklist)
    {
        if (await EnsureCanReadArrivalAsync(checklist.ArrivalId) is { } block) return block;
        var arrival = await _arrivals.GetAsync(checklist.ArrivalId);
        if (arrival == null) return NotFound();
        var editable = await FieldEditableAsync(arrival.StatusCode);
        // Block only when nothing on the checklist may be edited. Photo-taken
        // flags aren't in the registry, so editable("<no policy>") = admin-only —
        // preserving the old "Completed → admin only" behaviour for them.
        bool anyChecklistEditable =
            ArrivalFieldRegistry.All.Any(f => f.Form == ArrivalFieldRegistry.Checklist && editable(f.Key))
            || editable("__photo_flags__");   // true for Draft (all) or admin
        if (!anyChecklistEditable)
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
        // Merge: on a non-Draft arrival keep the stored value for any field the
        // user isn't allowed to change now (registry fields honour their policy;
        // the photo-taken flags stay admin-only via editable("__photo_flags__")).
        if (arrival.StatusCode != ArrivalStatus.Draft)
        {
            var cur = await _arrivals.GetChecklistAsync(checklist.ArrivalId) ?? checklist;
            if (!editable("seal_no"))                       checklist.SealNo                     = cur.SealNo;
            if (!editable("seal_intact"))                   checklist.SealIntact                 = cur.SealIntact;
            if (!editable("seal_matches_documents"))        checklist.SealMatchesDocuments       = cur.SealMatchesDocuments;
            if (!editable("external_damage_exists"))        checklist.ExternalDamageExists       = cur.ExternalDamageExists;
            if (!editable("set_temperature"))               checklist.SetTemperature             = cur.SetTemperature;
            if (!editable("display_temperature"))           checklist.DisplayTemperature         = cur.DisplayTemperature;
            if (!editable("cargo_smell_normal"))            checklist.CargoSmellNormal           = cur.CargoSmellNormal;
            if (!editable("visual_cargo_acceptable"))       checklist.VisualCargoAcceptable      = cur.VisualCargoAcceptable;
            if (!editable("cargo_shifted_collapsed_water")) checklist.CargoShiftedCollapsedWater = cur.CargoShiftedCollapsedWater;
            if (!editable("pulp_temp_front"))               checklist.PulpTempFront              = cur.PulpTempFront;
            if (!editable("pulp_temp_middle"))              checklist.PulpTempMiddle             = cur.PulpTempMiddle;
            if (!editable("pulp_temp_back"))                checklist.PulpTempBack               = cur.PulpTempBack;
            if (!editable("data_logger_located"))           checklist.DataLoggerLocated          = cur.DataLoggerLocated;
            if (!editable("data_logger_serial"))            checklist.DataLoggerSerial           = cur.DataLoggerSerial;
            if (!editable("logger_handed_over"))            checklist.LoggerHandedOver           = cur.LoggerHandedOver;
            if (!editable("logger_active_data_available"))  checklist.LoggerActiveDataAvailable  = cur.LoggerActiveDataAvailable;
            if (!editable("logger_temperature"))            checklist.LoggerTemperature          = cur.LoggerTemperature;
            if (!editable("notes"))                         checklist.Notes                      = cur.Notes;
            // Photo-taken flags: admin-only on a closed arrival.
            if (!editable("__photo_flags__"))
            {
                checklist.DataLoggerPhotoTaken          = cur.DataLoggerPhotoTaken;
                checklist.DisplayTempPhotoTaken         = cur.DisplayTempPhotoTaken;
                checklist.InternalInspectionPhotoTaken  = cur.InternalInspectionPhotoTaken;
                checklist.PulpTempPhotoTaken            = cur.PulpTempPhotoTaken;
                checklist.ContainerSealPhotoTaken       = cur.ContainerSealPhotoTaken;
                checklist.ExternalContainerPhotoTaken   = cur.ExternalContainerPhotoTaken;
                checklist.ExternalDamagePhotoTaken      = cur.ExternalDamagePhotoTaken;
                checklist.FirstViewCargoPhotoTaken      = cur.FirstViewCargoPhotoTaken;
                checklist.InternalDamagePhotoTaken      = cur.InternalDamagePhotoTaken;
            }
        }
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
    [RequirePermission(Perm.Arrivals.SaveShipment, Seed.OperatorOrAbove, "Save shipment details")]
    public async Task<IActionResult> SaveShipment(ShipmentSnapshot shipment)
    {
        if (await EnsureCanReadArrivalAsync(shipment.ArrivalId) is { } block) return block;
        var arrival = await _arrivals.GetAsync(shipment.ArrivalId);
        if (arrival == null) return NotFound();
        var editable = await FieldEditableAsync(arrival.StatusCode);
        // Block only when NOTHING on this form may be edited (Cancelled, or a
        // Completed arrival with no editable-when-closed field and not an admin).
        if (!ArrivalFieldRegistry.All.Any(f => f.Form == ArrivalFieldRegistry.Shipment && editable(f.Key)))
        {
            TempData["Error"] = $"Arrival is {arrival.StatusCode} — not editable.";
            return RedirectToAction(nameof(Details), new { id = shipment.ArrivalId });
        }
        // Keep the stored value for any field the user can't change now, so a
        // crafted post can't slip a locked field through.
        var cur = await _arrivals.GetShipmentAsync(shipment.ArrivalId) ?? new ShipmentSnapshot { ArrivalId = shipment.ArrivalId };
        if (!editable("discharge_date"))    shipment.DischargeDate   = cur.DischargeDate;
        if (!editable("unloading_date"))    shipment.UnloadingDate   = cur.UnloadingDate;
        if (!editable("pullout_date"))      shipment.PullOutDate     = cur.PullOutDate;
        if (!editable("time_bar"))          shipment.TimeBar         = cur.TimeBar;
        if (!editable("arrival_place"))     shipment.ArrivalPlace    = cur.ArrivalPlace;
        if (!editable("inspection_point"))  shipment.InspectionPoint = cur.InspectionPoint;
        if (!editable("joint_survey"))      shipment.JointSurvey     = cur.JointSurvey;
        if (!editable("time_bar_exceeded")) shipment.TimeBarExceeded = cur.TimeBarExceeded;
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _arrivals.SaveShipmentAsync(shipment, user);
        TempData["Success"] = "Shipment details saved.";
        return RedirectToAction(nameof(Details), new { id = shipment.ArrivalId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.Complete, Seed.OperatorOrAbove, "Complete an arrival")]
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

    /// <summary>
    /// Refuses a container that arrived damaged. One click ends the inspection
    /// and raises the claim order, because the claim window is short and a
    /// two-step flow leaves containers sitting rejected with nothing filed.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.Reject, Seed.SupervisorOrAbove, "Reject a container in bad condition")]
    public async Task<IActionResult> Reject(long id, string? reason)
    {
        if (await EnsureCanReadArrivalAsync(id) is { } block) return block;
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

        var (ok, error, qoId) = await _arrivals.RejectAsync(id, reason ?? "", user);
        if (!ok)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Details), new { id });
        }

        _logger.LogInformation("{User} rejected arrival {ArrivalId}; claim order {QoId} raised", user, id, qoId);
        TempData["Success"] = "Container rejected. A finished quality order carrying a potential claim "
                            + "was raised so the damage can go to the supplier.";
        // Straight to the order: the next thing anyone does is attach photos and
        // send it.
        return RedirectToAction("Details", "QualityOrders", new { id = qoId });
    }

    /// <summary>
    /// Undoes a rejection. Exists because nothing else can: a Closed quality
    /// order cannot be cancelled or deleted anywhere else in the application,
    /// so without this a mis-click would be permanent.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.CancelRejection, Seed.ManagerOrAdmin, "Undo a container rejection")]
    public async Task<IActionResult> CancelRejection(long id, string? reason)
    {
        if (await EnsureCanReadArrivalAsync(id) is { } block) return block;
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

        var (ok, error) = await _arrivals.CancelRejectionAsync(id, reason ?? "", user);
        TempData[ok ? "Success" : "Error"] = ok
            ? "Rejection undone. The arrival is back in Draft and its claim order is cancelled."
            : error;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Arrivals.ReopenForEdit, Seed.ManagerOrAdmin, "Reopen a completed arrival for editing")]
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
    [RequirePermission(Perm.Arrivals.Delete, Seed.ManagerOrAdmin, "Delete an arrival")]
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

}
