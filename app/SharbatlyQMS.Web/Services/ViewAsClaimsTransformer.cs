using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

// Swaps the Role claim in-memory when a SiteAdmin has the "view-as" cookie
// set. Lets a SiteAdmin test the UI/security checks as Viewer/Operator/Manager
// without re-logging-in. The cookie is ignored unless the underlying user is a
// real SiteAdmin, so non-admins can't elevate themselves by forging it.
public class ViewAsClaimsTransformer : IClaimsTransformation
{
    public const string CookieName        = "SharbatlyQMS.ViewAs";
    public const string OriginalRoleClaim = "OriginalRole";

    private readonly IHttpContextAccessor _http;

    public ViewAsClaimsTransformer(IHttpContextAccessor http) => _http = http;

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var ctx = _http.HttpContext;
        if (ctx == null || principal.Identity?.IsAuthenticated != true)
            return Task.FromResult(principal);

        var actualRole = principal.FindFirst(ClaimTypes.Role)?.Value;
        if (actualRole != UserRoles.SiteAdmin)
            return Task.FromResult(principal);

        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var viewAs)
            || string.IsNullOrEmpty(viewAs)
            || !UserRoles.IsValid(viewAs)
            || viewAs == actualRole)
            return Task.FromResult(principal);

        // Clone so we never mutate the cached principal (the framework may
        // call TransformAsync more than once with the same instance).
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
