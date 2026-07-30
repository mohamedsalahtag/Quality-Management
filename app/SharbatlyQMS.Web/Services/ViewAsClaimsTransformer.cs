using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Swaps the role claim in memory when someone with the "preview as" permission
/// has the view-as cookie set. It lets an administrator check what a role can
/// actually see without signing in as somebody else — which, now that roles are
/// composed rather than fixed, is the only practical way to verify a new one.
///
/// The cookie can never elevate: the guard asks the resolver what the user's
/// REAL role may do, and the resolver answers from the database rather than from
/// the claim the cookie carries.
/// </summary>
public class ViewAsClaimsTransformer : IClaimsTransformation
{
    public const string CookieName        = "SharbatlyQMS.ViewAs";
    public const string OriginalRoleClaim = "OriginalRole";

    private readonly IHttpContextAccessor _http;
    // Singleton, deliberately. IClaimsTransformation runs on every authenticated
    // request (sometimes more than once), so a scoped dependency here would be a
    // captive dependency and a per-request round trip.
    private readonly IPermissionResolver _perms;

    public ViewAsClaimsTransformer(IHttpContextAccessor http, IPermissionResolver perms)
    {
        _http  = http;
        _perms = perms;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var ctx = _http.HttpContext;
        if (ctx == null || principal.Identity?.IsAuthenticated != true)
            return principal;

        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var viewAs)
            || string.IsNullOrWhiteSpace(viewAs))
            return principal;

        await _perms.EnsureLoadedAsync();

        // The user's real role, resolved from their user id -- not from the role
        // claim, which this transformer may itself have rewritten on a previous
        // pass through the pipeline.
        var actualRole = UserPermissions.ResolveActualRole(_perms, principal);
        if (actualRole == null) return principal;

        if (!_perms.RoleHas(actualRole, Perm.Admin.ViewAs, AccessLevel.Edit, isScreen: false))
            return principal;

        // The target must be a role that actually exists and is active, or the
        // preview would silently resolve to "no permissions at all" and look
        // like a bug in the role being tested.
        var target = _perms.Roles.FirstOrDefault(r =>
            r.IsActive && string.Equals(r.RoleCode, viewAs, StringComparison.OrdinalIgnoreCase));
        if (target == null || string.Equals(target.RoleCode, actualRole, StringComparison.OrdinalIgnoreCase))
            return principal;

        // Clone so the cached principal is never mutated -- the framework may
        // call TransformAsync more than once with the same instance.
        var clone = principal.Clone();
        if (clone.Identity is not ClaimsIdentity identity) return principal;

        foreach (var existing in identity.FindAll(ClaimTypes.Role).ToList())
            identity.RemoveClaim(existing);
        identity.AddClaim(new Claim(ClaimTypes.Role, target.RoleCode));
        identity.AddClaim(new Claim(OriginalRoleClaim, actualRole));
        return clone;
    }
}
