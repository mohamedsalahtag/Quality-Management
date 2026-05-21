// AccountController.ViewAs - SPEC.md §2 (Endpoint contract)
//
// Paste this action method into the host project's auth/account controller.
// Requirements at the controller level:
//   * `using System.Security.Claims;` (for ClaimTypes)
//   * `using Microsoft.AspNetCore.Mvc;` (for IActionResult / Controller)
//   * Antiforgery middleware enabled globally OR the calling form supplies a
//     [ValidateAntiForgeryToken]-compatible token.
//   * `ViewAsClaimsTransformer` (from reference/ViewAsClaimsTransformer.cs)
//     in scope so the constants resolve.
//   * `UserRoles.IsValid(string)` available (typically from the host project's
//     Models namespace; otherwise install user-management-pack first).
//
// The Role-claim swap is done by ViewAsClaimsTransformer on every request;
// this action only manages the cookie that drives it. Passing role="" (or
// any non-role value) clears the cookie and the admin reverts to self.

[HttpPost, ValidateAntiForgeryToken]
public IActionResult ViewAs(string? role, string? returnUrl = null)
{
    // Trust OriginalRole when present (we are mid-impersonation), otherwise
    // the current Role claim is the real one. Inverting this lets an admin
    // lose the ability to toggle out -- see SPEC.md §6 hard rules.
    var realRole = User.FindFirst(ViewAsClaimsTransformer.OriginalRoleClaim)?.Value
                ?? User.FindFirst(ClaimTypes.Role)?.Value;
    if (realRole != ViewAsClaimsTransformer.ImpersonatorRole) return Forbid();

    // Empty / self / unknown role = clear impersonation (toggle off).
    if (string.IsNullOrWhiteSpace(role)
        || role == ViewAsClaimsTransformer.ImpersonatorRole
        || !UserRoles.IsValid(role))
    {
        Response.Cookies.Delete(ViewAsClaimsTransformer.CookieName);
    }
    else
    {
        Response.Cookies.Append(ViewAsClaimsTransformer.CookieName, role, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure   = Request.IsHttps,
            MaxAge   = TimeSpan.FromHours(8)
        });
    }

    if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        return Redirect(returnUrl);
    return RedirectToAction("Index", "Home");
}
