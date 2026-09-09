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

[Authorize]
public class ImagesController : Controller
{
    private readonly IImageService _images;
    private readonly IArrivalService _arrivals;
    private readonly IQualityOrderService _qos;
    private readonly IAuditService _audit;
    private readonly IUserPermissions _me;

    public ImagesController(IImageService images, IArrivalService arrivals,
        IQualityOrderService qos, IAuditService audit, IUserPermissions me)
    {
        _images = images; _arrivals = arrivals; _qos = qos; _audit = audit; _me = me;
    }

    /// <summary>
    /// The signed-off override. Removing a photo is normally Draft/Open-only --
    /// once a record is signed off its photo set is the evidence and stays
    /// append-only. This permission is the deliberate exception for a wrong or
    /// accidental upload, seeded to administrators only.
    ///
    /// Checked in ONE place, inside <see cref="GateOwnerAsync"/>, so the button
    /// and the endpoint can never disagree: the AJAX redraw derives AllowDelete
    /// by calling the same gate.
    /// </summary>
    private bool CanDeleteAfterClose => _me.Can(Perm.Attachments.PhotoDeleteAfterClose);

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Attachments.PhotoUpload, Seed.OperatorOrAbove, "Upload a photo")]

    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<IActionResult> Upload(string ownerType, long ownerId, string category,
        List<IFormFile> files, string? returnUrl)
    {
        // Gate: valid owner type (also prevents path traversal), owner exists,
        // parent record accepts new photos, and (if plant-scoped) in the
        // caller's plant.
        if (await GateOwnerAsync(ownerType, ownerId, ImageOp.Add) is { } block) return block;

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var saved = await _images.UploadAsync(ownerType, ownerId, category, files, user);

        if (saved > 0)
        {
            // Stamp the parent's status on the audit row. Photos added after the
            // arrival was completed are the exception path, so the audit trail
            // has to say so plainly rather than looking like an ordinary upload.
            var status = await OwnerStatusAsync(ownerType, ownerId);
            await SafeAuditAsync(ownerType, ownerId, ActionCodes.Created,
                newValues: new
                {
                    category,
                    files = saved,
                    ownerStatus = status,
                    afterCompletion = string.Equals(status, ArrivalStatus.Completed, StringComparison.OrdinalIgnoreCase)
                },
                actor: user);
        }

        if (IsAjax) return await GridPartialAsync(ownerType, ownerId);
        TempData[saved > 0 ? "Success" : "Error"] = saved > 0
            ? $"Uploaded {saved} image(s)."
            : "No files were saved (check size and file type).";
        return SafeRedirect(returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Attachments.PhotoDelete, Seed.OperatorOrAbove, "Delete a photo")]
    public async Task<IActionResult> Delete(long imageLinkId, string? ownerType, long ownerId, string? returnUrl)
    {
        // Resolve the link's real owner and gate against THAT (not the caller-
        // supplied ownerType/ownerId, which are only used for the grid redraw).
        var owner = await _images.GetLinkOwnerAsync(imageLinkId);
        if (owner == null)
        {
            if (IsAjax && !string.IsNullOrEmpty(ownerType)) return await GridPartialAsync(ownerType, ownerId);
            TempData["Error"] = "Image not found (it may already have been removed).";
            return SafeRedirect(returnUrl);
        }
        if (await GateOwnerAsync(owner.Value.ownerType, owner.Value.ownerId, ImageOp.Remove) is { } block) return block;

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _images.SoftDeleteLinkAsync(imageLinkId, user);
        await SafeAuditAsync(owner.Value.ownerType, owner.Value.ownerId, ActionCodes.Deleted,
            oldValues: new { imageLinkId }, actor: user);

        if (IsAjax && !string.IsNullOrEmpty(ownerType)) return await GridPartialAsync(ownerType, ownerId);
        TempData["Success"] = "Image removed.";
        return SafeRedirect(returnUrl);
    }

    /// <summary>Which side of the gallery is being exercised. Adding a photo and
    /// removing one no longer share the same rule on arrivals.</summary>
    private enum ImageOp { Add, Remove }

    // Resolves the owner record, confirms the operation is allowed in the
    // parent's current status, and enforces plant scope. Returns a non-null
    // IActionResult to short-circuit on any failure; null means "allowed".
    private async Task<IActionResult?> GateOwnerAsync(string ownerType, long ownerId, ImageOp op)
    {
        if (!ImageService.IsValidOwnerType(ownerType))
            return BadRequest("Unknown image owner type.");

        var scope = User.GetPlantScope();

        switch (ownerType)
        {
            case "Arrival":
            case "ArrivalChecklist":
            {
                var arrival = await _arrivals.GetAsync(ownerId);
                if (arrival == null) return NotFound();

                // Supporting photos may be ADDED after the checklist is
                // completed -- the evidence shot often arrives late. Every such
                // upload is audited with the arrival's status so the late
                // addition is visible on the record. Removing a photo stays
                // Draft-only: once the checklist is signed off, the photo set is
                // append-only. A Cancelled arrival is closed to both.
                // Rejected is included deliberately: the damage photographs are
                // the whole substance of the claim and they are usually taken
                // after the container has already been refused.
                var addAllowed = arrival.StatusCode == ArrivalStatus.Draft
                              || arrival.StatusCode == ArrivalStatus.Completed
                              || arrival.StatusCode == ArrivalStatus.Rejected;
                if (op == ImageOp.Add && !addAllowed)
                    return EditBlocked($"Arrival is {arrival.StatusCode}; photos cannot be added.");
                if (op == ImageOp.Remove && arrival.StatusCode != ArrivalStatus.Draft
                    && !CanDeleteAfterClose)
                    return EditBlocked($"Arrival is {arrival.StatusCode}; photos can no longer be removed, only added.");

                if (!scope.Unrestricted && !scope.Allows(await _arrivals.GetPlantAsync(ownerId)))
                    return Forbid();
                return null;
            }
            case "Sample":
            {
                var sample = await _qos.GetSampleAsync(ownerId);
                if (sample == null) return NotFound();
                var qo = await _qos.GetAsync(sample.QualityOrderId);
                if (qo == null) return NotFound();
                // Adding is Open-only for everyone; removing has the
                // signed-off override, same as an arrival.
                if (qo.StatusCode != QualityOrderStatus.Open
                    && !(op == ImageOp.Remove && CanDeleteAfterClose))
                    return EditBlocked($"Quality Order is {qo.StatusCode}; photos can only be changed while it is Open.");
                if (!scope.Unrestricted && !scope.Allows(await _qos.GetPlantForQoAsync(sample.QualityOrderId)))
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

    // Status of the owning record, recorded on the audit row. Null when the
    // owner type has no status of its own.
    private async Task<string?> OwnerStatusAsync(string ownerType, long ownerId) => ownerType switch
    {
        "Arrival" or "ArrivalChecklist" => (await _arrivals.GetAsync(ownerId))?.StatusCode,
        "Sample" => await _qos.GetSampleAsync(ownerId) is { } sample
                        ? (await _qos.GetAsync(sample.QualityOrderId))?.StatusCode
                        : null,
        _ => null
    };

    // The AJAX redraw re-derives AllowDelete, otherwise a Completed arrival
    // would grow delete buttons the moment a photo is uploaded.
    private async Task<PartialViewResult> GridPartialAsync(string ownerType, long ownerId)
    {
        var canDelete = await GateOwnerAsync(ownerType, ownerId, ImageOp.Remove) == null;
        return PartialView("_ImageGalleryGrid", new ImageGalleryVm
        {
            OwnerType   = ownerType,
            OwnerId     = ownerId,
            Editable    = true,
            AllowDelete = canDelete
        });
    }

    private IActionResult SafeRedirect(string? url)
    {
        if (!string.IsNullOrEmpty(url) && Url.IsLocalUrl(url)) return Redirect(url);
        return RedirectToAction("Index", "Home");
    }
}
