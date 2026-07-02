using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

[Authorize]
public class ImagesController : Controller
{
    private readonly IImageService _images;
    private readonly IArrivalService _arrivals;
    private readonly IQualityOrderService _qos;
    private readonly IAuditService _audit;

    public ImagesController(IImageService images, IArrivalService arrivals,
        IQualityOrderService qos, IAuditService audit)
    {
        _images = images; _arrivals = arrivals; _qos = qos; _audit = audit;
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<IActionResult> Upload(string ownerType, long ownerId, string category,
        List<IFormFile> files, string? returnUrl)
    {
        // Gate: valid owner type (also prevents path traversal), owner exists,
        // parent record is editable, and (if plant-scoped) in the caller's plant.
        if (await GateOwnerAsync(ownerType, ownerId) is { } block) return block;

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var saved = await _images.UploadAsync(ownerType, ownerId, category, files, user);

        if (saved > 0)
            await SafeAuditAsync(ownerType, ownerId, ActionCodes.Created,
                newValues: new { category, files = saved }, actor: user);

        if (IsAjax) return GridPartial(ownerType, ownerId);
        TempData[saved > 0 ? "Success" : "Error"] = saved > 0
            ? $"Uploaded {saved} image(s)."
            : "No files were saved (check size and file type).";
        return SafeRedirect(returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Delete(long imageLinkId, string? ownerType, long ownerId, string? returnUrl)
    {
        // Resolve the link's real owner and gate against THAT (not the caller-
        // supplied ownerType/ownerId, which are only used for the grid redraw).
        var owner = await _images.GetLinkOwnerAsync(imageLinkId);
        if (owner == null)
        {
            if (IsAjax && !string.IsNullOrEmpty(ownerType)) return GridPartial(ownerType, ownerId);
            TempData["Error"] = "Image not found (it may already have been removed).";
            return SafeRedirect(returnUrl);
        }
        if (await GateOwnerAsync(owner.Value.ownerType, owner.Value.ownerId) is { } block) return block;

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _images.SoftDeleteLinkAsync(imageLinkId, user);
        await SafeAuditAsync(owner.Value.ownerType, owner.Value.ownerId, ActionCodes.Deleted,
            oldValues: new { imageLinkId }, actor: user);

        if (IsAjax && !string.IsNullOrEmpty(ownerType)) return GridPartial(ownerType, ownerId);
        TempData["Success"] = "Image removed.";
        return SafeRedirect(returnUrl);
    }

    // Resolves the owner record, confirms it is editable, and enforces plant
    // scope. Returns a non-null IActionResult to short-circuit on any failure;
    // null means "allowed".
    private async Task<IActionResult?> GateOwnerAsync(string ownerType, long ownerId)
    {
        if (!ImageService.IsValidOwnerType(ownerType))
            return BadRequest("Unknown image owner type.");

        var scoped = User.GetScopedPlant();

        switch (ownerType)
        {
            case "Arrival":
            case "ArrivalChecklist":
            {
                var arrival = await _arrivals.GetAsync(ownerId);
                if (arrival == null) return NotFound();
                if (arrival.StatusCode != ArrivalStatus.Draft)
                    return EditBlocked($"Arrival is {arrival.StatusCode}; photos can only be changed while it is in Draft.");
                if (scoped != null && !string.Equals(await _arrivals.GetPlantAsync(ownerId), scoped, StringComparison.OrdinalIgnoreCase))
                    return Forbid();
                return null;
            }
            case "Sample":
            {
                var sample = await _qos.GetSampleAsync(ownerId);
                if (sample == null) return NotFound();
                var qo = await _qos.GetAsync(sample.QualityOrderId);
                if (qo == null) return NotFound();
                if (qo.StatusCode != QualityOrderStatus.Open)
                    return EditBlocked($"Quality Order is {qo.StatusCode}; photos can only be changed while it is Open.");
                if (scoped != null && !string.Equals(await _qos.GetPlantForQoAsync(sample.QualityOrderId), scoped, StringComparison.OrdinalIgnoreCase))
                    return Forbid();
                return null;
            }
            default:
                // Whitelisted but not reachable from the UI; the whitelist has
                // already made the storage path safe.
                return null;
        }
    }

    private IActionResult EditBlocked(string message)
    {
        if (IsAjax) return Json(new { ok = false, error = message });
        TempData["Error"] = message;
        return SafeRedirect(null);
    }

    // Best-effort audit for image attach/detach; a failure must not fail the op.
    private async Task SafeAuditAsync(string ownerType, long ownerId, string action,
        object? oldValues = null, object? newValues = null, string actor = "system")
    {
        var entityType = ownerType switch
        {
            "Arrival" or "ArrivalChecklist" => EntityTypes.Arrival,
            "Sample"                        => EntityTypes.Sample,
            "QualityOrderMaterial"          => EntityTypes.QualityOrderMaterial,
            _                               => ownerType
        };
        try
        {
            await _audit.WriteAsync(entityType, ownerId, action, oldValues, newValues, actor);
        }
        catch { /* audit is best-effort here; the image op already succeeded */ }
    }

    private bool IsAjax => Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    private PartialViewResult GridPartial(string ownerType, long ownerId) =>
        PartialView("_ImageGalleryGrid", new ImageGalleryVm
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
