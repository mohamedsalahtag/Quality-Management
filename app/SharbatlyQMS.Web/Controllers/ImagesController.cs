using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

[Authorize]
public class ImagesController : Controller
{
    private readonly IImageService _images;
    public ImagesController(IImageService images) => _images = images;

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<IActionResult> Upload(string ownerType, long ownerId, string category,
        List<IFormFile> files, string? returnUrl)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var saved = await _images.UploadAsync(ownerType, ownerId, category, files, user);
        TempData[saved > 0 ? "Success" : "Error"] = saved > 0
            ? $"Uploaded {saved} image(s)."
            : "No files were saved (check size and file type).";
        return SafeRedirect(returnUrl);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Delete(long imageLinkId, string? returnUrl)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _images.SoftDeleteLinkAsync(imageLinkId, user);
        TempData["Success"] = "Image removed.";
        return SafeRedirect(returnUrl);
    }

    private IActionResult SafeRedirect(string? url)
    {
        if (!string.IsNullOrEmpty(url) && Url.IsLocalUrl(url)) return Redirect(url);
        return RedirectToAction("Index", "Home");
    }
}
