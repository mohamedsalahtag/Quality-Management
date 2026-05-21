// ViewAsClaimsTransformer - lets a real SiteAdmin impersonate any other
// role for testing (Viewer, Operator, Manager, ...). On every authenticated
// request, this transformer reads the "view-as" cookie and swaps the
// ClaimTypes.Role claim in-memory so [Authorize], User.IsInRole, and every
// role-gated menu respond as if the user were the impersonated role.
//
// Security model:
//   * The cookie is honored ONLY when the user's REAL role claim equals
//     the configured admin role (SiteAdmin by default). A non-admin who
//     manually forges this cookie gets no elevation.
//   * The original role is preserved in a second claim (OriginalRole) so
//     the controller can validate that toggling off is allowed.
//   * The cookie itself only stores the desired role string -- no secret
//     material -- so it's HttpOnly + SameSite=Lax and short-lived.
//
// SPEC.md §6 (View site as) covers the full behavioral contract.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using HelpDesk.Models;

namespace HelpDesk.Services;

public class ViewAsClaimsTransformer : IClaimsTransformation
{
    // Cookie + claim names. Rename per project (e.g. SharbatlyQMS.ViewAs).
    public const string CookieName        = "HelpDesk.ViewAs";
    public const string OriginalRoleClaim = "OriginalRole";

    // The "real" role that is allowed to impersonate other roles.
    // Default is SiteAdmin -- change if your installation uses a different
    // top-level role name.
    public const string ImpersonatorRole  = UserRoles.SiteAdmin;

    private readonly IHttpContextAccessor _http;

    public ViewAsClaimsTransformer(IHttpContextAccessor http) => _http = http;

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var ctx = _http.HttpContext;
        if (ctx == null || principal.Identity?.IsAuthenticated != true)
            return Task.FromResult(principal);

        var actualRole = principal.FindFirst(ClaimTypes.Role)?.Value;
        if (actualRole != ImpersonatorRole)
            return Task.FromResult(principal);

        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var viewAs)
            || string.IsNullOrEmpty(viewAs)
            || !UserRoles.IsValid(viewAs)
            || viewAs == actualRole)
            return Task.FromResult(principal);

        // Clone so we never mutate the cached principal -- the framework
        // may call TransformAsync multiple times with the same instance.
        var clone = principal.Clone();
        if (clone.Identity is not ClaimsIdentity identity)
            return Task.FromResult(principal);

        var existing = identity.FindFirst(ClaimTypes.Role);
        if (existing != null) identity.RemoveClaim(existing);
        identity.AddClaim(new Claim(ClaimTypes.Role, viewAs));
        identity.AddClaim(new Claim(OriginalRoleClaim, actualRole));
        return Task.FromResult(clone);
    }
}
