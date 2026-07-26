using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Document (non-image) attachments. Mirrors ImagesController, with one
/// deliberate difference: documents are stored outside wwwroot and served only
/// by the authenticated Download action below, so a supplier invoice is not
/// readable by anyone who happens to have the URL.
/// </summary>
[Authorize]
public class DocumentsController : Controller
{
    private readonly IDocumentService _docs;
    private readonly IArrivalService _arrivals;
    private readonly IQualityOrderService _qos;
    private readonly IAuditService _audit;

    public DocumentsController(IDocumentService docs, IArrivalService arrivals,
        IQualityOrderService qos, IAuditService audit)
    {
        _docs = docs; _arrivals = arrivals; _qos = qos; _audit = audit;
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<IActionResult> Upload(string ownerType, long ownerId, string category,
        List<IFormFile> files, string? returnUrl)
    {
        // Gate: valid owner type (also prevents path traversal), owner exists,
        // parent record is editable, and (if plant-scoped) in the caller's plant.
        if (await GateEditAsync(ownerType, ownerId) is { } block) return block;

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (saved, rejected) = await _docs.UploadAsync(ownerType, ownerId, category, files, user);

        if (saved > 0)
            await SafeAuditAsync(ownerType, ownerId, ActionCodes.Created,
                newValues: new { category, files = saved }, actor: user);

        if (IsAjax)
        {
            // Surface rejections in the re-rendered grid rather than silently
            // dropping the files and leaving the user to count rows. ViewData
            // (not TempData) — the partial renders in this same request.
            ViewData["DocUploadErrors"] = rejected;
            return GridPartial(ownerType, ownerId);
        }

        if (rejected.Count > 0)
            TempData["Error"] = $"{rejected.Count} file(s) not saved: {string.Join("; ", rejected)}";
        if (saved > 0)
            TempData["Success"] = $"Uploaded {saved} document(s).";
        else if (rejected.Count == 0)
            TempData["Error"] = "No files were selected.";

        return SafeRedirect(returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Delete(long documentId, string? ownerType, long ownerId, string? returnUrl)
    {
        // Resolve the document's real owner and gate against THAT (not the caller-
        // supplied ownerType/ownerId, which are only used for the grid redraw).
        var doc = await _docs.GetAsync(documentId);
        if (doc == null)
        {
            if (IsAjax && !string.IsNullOrEmpty(ownerType)) return GridPartial(ownerType, ownerId);
            TempData["Error"] = "Document not found (it may already have been removed).";
            return SafeRedirect(returnUrl);
        }
        if (await GateEditAsync(doc.OwnerType, doc.OwnerId) is { } block) return block;

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _docs.SoftDeleteAsync(documentId, user);
        await SafeAuditAsync(doc.OwnerType, doc.OwnerId, ActionCodes.Deleted,
            oldValues: new { documentId, doc.OriginalName }, actor: user);

        if (IsAjax && !string.IsNullOrEmpty(ownerType)) return GridPartial(ownerType, ownerId);
        TempData["Success"] = "Document removed.";
        return SafeRedirect(returnUrl);
    }

    /// <summary>
    /// The only way to read a document's bytes. Any authenticated user in the
    /// right plant may download — including Viewer, and including on a Completed
    /// arrival. Read access must NOT depend on the record still being editable.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Download(long id)
    {
        var doc = await _docs.GetAsync(id);
        if (doc == null) return NotFound();
        if (await GateReadAsync(doc.OwnerType, doc.OwnerId) is { } block) return block;

        var path = _docs.ResolveAbsolutePath(doc);
        if (path == null || !System.IO.File.Exists(path)) return NotFound();

        var stream = System.IO.File.OpenRead(path);
        // Content type is re-derived server-side from the extension; the stored
        // content_type is whatever the client claimed at upload. Always serve as
        // an attachment so a crafted .txt/.msg can't render as HTML in our origin.
        return File(stream, _docs.ContentTypeFor(doc.OriginalName), doc.OriginalName);
    }

    // ---- gates ---------------------------------------------------------
    // Two distinct gates. Edit requires the parent record to still be open;
    // read only requires the caller to be in the right plant. Collapsing them
    // would either hide invoices on completed arrivals or let anyone edit them.

    private async Task<IActionResult?> GateEditAsync(string ownerType, long ownerId)
    {
        if (!DocumentService.IsValidOwnerType(ownerType))
            return BadRequest("Unknown document owner type.");

        switch (ownerType)
        {
            case "Arrival":
            {
                // Documents may be attached/removed regardless of the arrival's
                // status (per user request) — the status gate was removed. The
                // OperatorOrAbove policy + plant scope are still enforced.
                var arrival = await _arrivals.GetAsync(ownerId);
                if (arrival == null) return NotFound();
                return await PlantGateAsync(ownerId);
            }
            case "QualityOrder":
            {
                // Documents may be attached/removed regardless of the QO's status
                // (per user request). Role policy + plant scope still apply.
                var qo = await _qos.GetAsync(ownerId);
                if (qo == null) return NotFound();
                return await QoPlantGateAsync(ownerId);
            }
            default:
                // Whitelisted but not reachable from the UI in V39; the whitelist
                // has already made the storage path safe.
                return null;
        }
    }

    private async Task<IActionResult?> GateReadAsync(string ownerType, long ownerId)
    {
        if (!DocumentService.IsValidOwnerType(ownerType))
            return BadRequest("Unknown document owner type.");

        return ownerType switch
        {
            "Arrival"      => await _arrivals.GetAsync(ownerId) == null ? NotFound() : await PlantGateAsync(ownerId),
            "QualityOrder" => await _qos.GetAsync(ownerId) == null ? NotFound() : await QoPlantGateAsync(ownerId),
            _              => null
        };
    }

    private async Task<IActionResult?> PlantGateAsync(long arrivalId)
    {
        var scoped = User.GetScopedPlant();
        if (scoped == null) return null;   // unrestricted role
        var plant = await _arrivals.GetPlantAsync(arrivalId);
        return string.Equals(plant, scoped, StringComparison.OrdinalIgnoreCase) ? null : Forbid();
    }

    private async Task<IActionResult?> QoPlantGateAsync(long qualityOrderId)
    {
        var scoped = User.GetScopedPlant();
        if (scoped == null) return null;   // unrestricted role
        var plant = await _qos.GetPlantForQoAsync(qualityOrderId);
        return string.Equals(plant, scoped, StringComparison.OrdinalIgnoreCase) ? null : Forbid();
    }

    private IActionResult EditBlocked(string message)
    {
        if (IsAjax) return Json(new { ok = false, error = message });
        TempData["Error"] = message;
        return SafeRedirect(null);
    }

    // Best-effort audit for document attach/detach; a failure must not fail the op.
    private async Task SafeAuditAsync(string ownerType, long ownerId, string action,
        object? oldValues = null, object? newValues = null, string actor = "system")
    {
        var entityType = ownerType switch
        {
            "Arrival"      => EntityTypes.Arrival,
            "QualityOrder" => EntityTypes.QualityOrder,
            "Sample"       => EntityTypes.Sample,
            _              => ownerType
        };
        try
        {
            await _audit.WriteAsync(entityType, ownerId, action, oldValues, newValues, actor);
        }
        catch { /* audit is best-effort here; the document op already succeeded */ }
    }

    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    private PartialViewResult GridPartial(string ownerType, long ownerId) =>
        PartialView("_DocumentListGrid", new DocumentListVm
        {
            OwnerType = ownerType,
            OwnerId   = ownerId,
            Editable  = true
        });

    private IActionResult SafeRedirect(string? url)
    {
        if (!string.IsNullOrEmpty(url) && Url.IsLocalUrl(url)) return Redirect(url);
        return RedirectToAction("Index", "Home");
    }
}
