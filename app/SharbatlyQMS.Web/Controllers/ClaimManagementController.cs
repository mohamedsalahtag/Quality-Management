using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
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
    private readonly ISettingsService _settings;
    private readonly ILogger<ClaimManagementController> _log;

    public ClaimManagementController(
        IClaimService claims,
        IQualityOrderService qos,
        IArrivalService arrivals,
        IImageService images,
        IMaraService mara,
        ISettingsService settings,
        ILogger<ClaimManagementController> log)
    {
        _claims = claims; _qos = qos; _arrivals = arrivals;
        _images = images; _mara = mara; _settings = settings; _log = log;
    }

    public async Task<IActionResult> Index(string? status, string? search)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var rows = await _claims.ListClosedQosAsync(
            string.IsNullOrEmpty(status) ? null : status, search, user);
        ViewBag.Status = status;
        ViewBag.Search = search;
        return View(rows);
    }

    /// <summary>
    /// Reuses Views/QualityOrders/Details.cshtml with ViewBag.IsClaimContext=true
    /// so the existing view collapses every mutation button and renders the
    /// claim chat panel at the bottom.
    /// </summary>
    public async Task<IActionResult> Details(long id)
    {
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

        var (claim, notes) = await _claims.GetForQoAsync(id);

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
        ViewBag.SendMailEnabled = false;             // suppress the supplier-email button
        ViewBag.IsClaimContext  = true;
        ViewBag.Claim           = claim;
        ViewBag.ClaimNotes      = notes;
        return View("/Views/QualityOrders/Details.cshtml", qo);
    }

    // ---- QM actions (Manager or SiteAdmin) ------------------------

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public Task<IActionResult> MarkClaimRequest(long id, string note)
        => ActAsync(id, note, _claims.MarkClaimRequestAsync, "Marked as Claim Request.");

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public Task<IActionResult> MarkPassedQc(long id, string note)
        => ActAsync(id, note, _claims.MarkPassedQcAsync, "Marked as Passed QC.");

    // ---- CM actions (ClaimManager or SiteAdmin) -------------------

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ClaimManagerOrAdmin)]
    public Task<IActionResult> Approve(long id, string note)
        => ActAsync(id, note, _claims.ApproveAsync, "Claim approved.");

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ClaimManagerOrAdmin)]
    public Task<IActionResult> Hold(long id, string note)
        => ActAsync(id, note, _claims.HoldAsync, "Claim placed on hold.");

    // ---- Either Manager/ClaimManager/SiteAdmin --------------------

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNote(long id, string note)
    {
        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? "";
        // Inline gate -- Viewer/Operator should not be able to post chat notes.
        if (role != UserRoles.Manager && role != UserRoles.ClaimManager && role != UserRoles.SiteAdmin)
            return Forbid();
        return await ActAsync(id, note, _claims.AddNoteAsync, "Note added.");
    }

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
