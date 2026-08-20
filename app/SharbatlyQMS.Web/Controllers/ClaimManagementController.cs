using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Post-QC claim workflow. Sits on top of every Closed Quality Order so a
/// Quality Manager can raise (or clear) a commercial claim and a Claim
/// Manager has the final decision (Approve / Hold). Read-only on the QO
/// itself -- this controller never edits inspection data.
///
/// Class-level gate is plain [Authorize] so any authenticated user can
/// browse the list and view the chat (read-only). Each mutating action
/// carries its own policy attribute -- mirrors the AdminController
/// pattern documented in PROJECT_STATE.md §6.
/// </summary>
[Authorize]
public class ClaimManagementController : Controller
{
    private readonly IClaimService _claims;
    private readonly IQualityOrderService _qos;
    private readonly IArrivalService _arrivals;
    private readonly IImageService _images;
    private readonly IMaraService _mara;
    private readonly ILogger<ClaimManagementController> _log;

    public ClaimManagementController(
        IClaimService claims,
        IQualityOrderService qos,
        IArrivalService arrivals,
        IImageService images,
        IMaraService mara,
        ILogger<ClaimManagementController> log)
    {
        _claims = claims; _qos = qos; _arrivals = arrivals;
        _images = images; _mara = mara; _log = log;
    }

    /// <summary>Archived orders — everything dated before the 2026-08-18 go-live
    /// cut-over, whatever its status. Same view, same filters; the tab strip
    /// switches between them. Kept as its own action rather than a query-string
    /// flag so the two lists get their own URLs and cannot be mixed.</summary>
    [RequireScreen(Screens.Claims, Seed.Everyone, "Open Claims")]
    public Task<IActionResult> Archived([FromQuery] ClaimListFilter filter)
    {
        filter.Archived = true;
        return ListAsync(filter);
    }

    [RequireScreen(Screens.Claims, Seed.Everyone, "Open Claims")]
    public Task<IActionResult> Index([FromQuery] ClaimListFilter filter)
    {
        filter.Archived = false;
        return ListAsync(filter);
    }

    private async Task<IActionResult> ListAsync(ClaimListFilter filter)
    {
        // Plant-scoped users can't widen their view: the scope is applied in
        // the query regardless of what the panel's plant dropdown posted (the
        // view renders a locked badge + hidden input to match).
        var user    = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var scope   = User.GetPlantScope();
        var rows    = await _claims.ListClosedQosAsync(filter, scope, user);
        var options = await _claims.GetClaimFilterOptionsAsync(scope, filter.Archived);
        ViewBag.Filter           = filter;
        ViewBag.FilterOptions    = options;
        ViewBag.PlantScopeLocked = User.SinglePlantOrNull();
        // Both actions render the Claims list; the tab strip reads Filter.Archived.
        return View("Index", rows);
    }

    /// <summary>
    /// Reuses Views/QualityOrders/Details.cshtml with ViewBag.IsClaimContext=true
    /// so the existing view collapses every mutation button and renders the
    /// claim chat panel at the bottom.
    /// </summary>
    [RequireScreen(Screens.Claims)]
    public async Task<IActionResult> Details(long id)
    {
        // Plant-scoped users must not see claims for QOs in other plants.
        // Administrators (and anyone marked all-plants) are unrestricted and pass.
        var scope = User.GetPlantScope();
        if (!scope.Unrestricted)
        {
            var qoPlant = await _qos.GetPlantForQoAsync(id);
            if (!scope.Allows(qoPlant)) return Forbid();
        }
        var qo = await _qos.GetAsync(id);
        if (qo == null) return NotFound();

        // Mirrors QualityOrdersController.Details (lines 55-87) but always
        // forces read-only by setting Editable=false. Claim panel is the
        // only interactive surface.
        var arrival   = await _arrivals.GetAsync(qo.ArrivalId);
        var shipment  = await _arrivals.GetShipmentAsync(qo.ArrivalId);
        var materials = (await _qos.GetMaterialsAsync(id)).ToList();
        var samples   = await _qos.ListSamplesAsync(id);

        var mara = await _mara.LookupAsync(materials.Select(m => m.MaterialNo));
        foreach (var m in materials)
            if (mara.TryGetValue(m.MaterialNo, out var mm)) m.ApplyMara(mm);

        var photoCounts = await _images.CountByOwnersAsync(
            "QualityOrderMaterial", materials.Select(m => m.QoMaterialId));

        // Grouped summary — the SAME rollup the QC report prints on page 1
        // (BuildGroupSummariesAsync), so the claim page's "Summary" header matches
        // the report exactly. Materials are already MARA-enriched above, so the
        // grouping key (group/brand/variety/grade) reflects the live cache.
        var reportUnits    = await _qos.GetReportUnitsAsync();
        var groupSummaries = await _qos.BuildGroupSummariesAsync(id, materials, reportUnits);

        var (claim, notes) = await _claims.GetForQoAsync(id);

        // The finisher's optional comment (stored as the QO close reason) shows
        // as the first chat message on the claim page, attributed to whoever
        // finished the order — visible even before a formal claim is opened.
        IReadOnlyList<Models.ClaimNote> chatNotes = notes;
        if (!string.IsNullOrWhiteSpace(qo.CloseReason))
        {
            var finishNote = new Models.ClaimNote
            {
                NoteText   = qo.CloseReason!,
                NoteKind   = Models.ClaimNoteKind.Comment,
                CreatedAt  = qo.ClosedAt ?? qo.CreatedAt,
                CreatedBy  = qo.ClosedBy ?? "system",
                AuthorRole = "Finish note"
            };
            chatNotes = new[] { finishNote }.Concat(notes).ToList();
        }

        // Bump this user's read-marker so any "new" badges on the list page
        // for this claim disappear after they view the chat. No-op when the
        // QO has no claim row yet (MarkSeenAsync just won't find anything).
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _claims.MarkSeenAsync(id, user);


        ViewBag.Arrival         = arrival;
        ViewBag.Shipment        = shipment;
        ViewBag.Materials       = materials;
        ViewBag.Samples         = samples;
        ViewBag.PhotoCounts     = photoCounts;
        ViewBag.Editable        = false;             // hard-locked in claim context
        ViewBag.IsClaimContext  = true;
        ViewBag.GroupSummaries  = groupSummaries;
        ViewBag.Claim           = claim;
        ViewBag.ClaimNotes      = chatNotes;
        return View("/Views/QualityOrders/Details.cshtml", qo);
    }

    // ---- QM actions (Manager or SiteAdmin) ------------------------

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Claims.MarkClaimRequest, Seed.ManagerOrAdmin, "Mark as claim request")]
    public Task<IActionResult> MarkClaimRequest(long id, string note)
        => ActAsync(id, note, _claims.MarkClaimRequestAsync, "Marked as Claim Request.");

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Claims.MarkPassedQc, Seed.ManagerOrAdmin, "Mark as passed QC")]
    public Task<IActionResult> MarkPassedQc(long id, string note)
        => ActAsync(id, note, _claims.MarkPassedQcAsync, "Marked as Passed QC.");

    // ---- CM actions (ClaimManager or SiteAdmin) -------------------

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Claims.Approve, Seed.ClaimManagerOrAdmin, "Approve a claim")]
    public Task<IActionResult> Approve(long id, string note)
        => ActAsync(id, note, _claims.ApproveAsync, "Claim approved.");

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Claims.Hold, Seed.ClaimManagerOrAdmin, "Put a claim on hold")]
    public Task<IActionResult> Hold(long id, string note)
        => ActAsync(id, note, _claims.HoldAsync, "Claim placed on hold.");

    // ---- Either Manager/ClaimManager/SiteAdmin --------------------

    // This was the only mutating action in the application gated by an inline
    // role comparison rather than an attribute -- invisible to any audit of the
    // authorization surface. It is now an ordinary permission.
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Claims.AddNote, Seed.ManagerOrClaimManagerOrAdmin, "Post a note on a claim")]
    public Task<IActionResult> AddNote(long id, string note)
        => ActAsync(id, note, _claims.AddNoteAsync, "Note added.");

    // ---- Shared plumbing ------------------------------------------

    private async Task<IActionResult> ActAsync(
        long id, string note,
        Func<long, string, string, string, Task<(bool ok, string? error)>> serviceCall,
        string okFlash)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        var (ok, err) = await serviceCall(id, note ?? "", user, role);
        if (ok) TempData["Success"] = okFlash;
        else    TempData["Error"]   = err ?? "Action failed.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
