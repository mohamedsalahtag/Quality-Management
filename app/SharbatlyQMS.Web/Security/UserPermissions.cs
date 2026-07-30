using System.Security.Claims;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Security;

/// <summary>
/// The per-request view of what the signed-in user may do. Injected into every
/// Razor view as <c>Perms</c> and into controllers that need to decide
/// server-side; it replaces the thirteen ad-hoc role variables the views used
/// to compute for themselves.
/// </summary>
public interface IUserPermissions
{
    /// <summary>The role whose permissions are in force — the impersonated role
    /// while previewing, otherwise the user's own.</summary>
    string? EffectiveRole { get; }

    /// <summary>The user's real role, ignoring any impersonation.</summary>
    string? ActualRole { get; }

    bool IsImpersonating { get; }

    /// <summary>Holds an action permission.</summary>
    bool Can(string permissionCode);

    /// <summary>May open a screen at all.</summary>
    bool CanView(string screenKey);

    /// <summary>May change things on a screen.</summary>
    bool CanEdit(string screenKey);

    AccessLevel Level(string screenKey);

    /// <summary>May open at least one of these screens — for a nav dropdown.</summary>
    bool CanViewAny(params string[] screenKeys);

    /// <summary>Emits the <c>disabled</c> attribute when the screen is read-only,
    /// for <c>&lt;fieldset @Perms.DisabledUnlessEdit(...)&gt;</c>.</summary>
    string DisabledUnlessEdit(string screenKey);

    /// <summary>Decisions as the user's REAL role, ignoring impersonation. The
    /// escape hatch that keeps "stop previewing" reachable.</summary>
    IUserPermissions AsActualUser { get; }

    /// <summary>Active roles that can be previewed, for the "View as" menu.
    /// Sourced from the role table, so a role composed on the Security screen
    /// is previewable the moment it exists.</summary>
    IReadOnlyList<RoleInfo> PreviewableRoles { get; }
}

public sealed class UserPermissions : IUserPermissions
{
    private readonly IPermissionResolver _resolver;
    private readonly string? _actualRole;
    private readonly string? _effectiveRole;

    public UserPermissions(IPermissionResolver resolver, IHttpContextAccessor http)
    {
        _resolver = resolver;
        var user = http.HttpContext?.User;

        // Resolve the real role from the USER ID, not the role claim: the claim
        // rides a 30-day cookie and can be stale, renamed or deleted.
        _actualRole = ResolveActualRole(resolver, user);

        // ViewAsClaimsTransformer swaps the role claim when a site administrator
        // is previewing another role. Honour it only if the REAL role may do so.
        var impersonated = user?.FindFirst(ViewAsClaimsTransformer.OriginalRoleClaim) != null
            ? user.FindFirst(ClaimTypes.Role)?.Value
            : null;
        _effectiveRole =
            impersonated != null
            && !string.Equals(impersonated, _actualRole, StringComparison.OrdinalIgnoreCase)
            && resolver.RoleHas(_actualRole, Perm.Admin.ViewAs, AccessLevel.Edit, isScreen: false)
                ? impersonated
                : _actualRole;
    }

    private UserPermissions(IPermissionResolver resolver, string? actualRole)
    {
        _resolver = resolver;
        _actualRole = _effectiveRole = actualRole;
    }

    internal static string? ResolveActualRole(IPermissionResolver resolver, ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (int.TryParse(id, out var userId))
        {
            var fromDb = resolver.RoleCodeOf(userId);
            if (fromDb != null) return fromDb;
        }
        // Fallback for principals with no resolvable user id (the test auth
        // handler, and any future non-cookie scheme). An impersonated claim can
        // only ever narrow access here, never widen it, because the ViewAs check
        // below still has to pass.
        return user.FindFirst(ViewAsClaimsTransformer.OriginalRoleClaim)?.Value
               ?? user.FindFirst(ClaimTypes.Role)?.Value;
    }

    public string? EffectiveRole   => _effectiveRole;
    public string? ActualRole      => _actualRole;
    public bool    IsImpersonating => !string.Equals(_effectiveRole, _actualRole, StringComparison.OrdinalIgnoreCase);

    public bool Can(string permissionCode) =>
        _resolver.RoleHas(_effectiveRole, permissionCode, AccessLevel.Edit, isScreen: false);

    public bool CanView(string screenKey) =>
        _resolver.RoleHas(_effectiveRole, screenKey, AccessLevel.Read, isScreen: true);

    public bool CanEdit(string screenKey) =>
        _resolver.RoleHas(_effectiveRole, screenKey, AccessLevel.Edit, isScreen: true);

    public AccessLevel Level(string screenKey) => _resolver.RawLevel(_effectiveRole, screenKey);

    public bool CanViewAny(params string[] screenKeys) => screenKeys.Any(CanView);

    public string DisabledUnlessEdit(string screenKey) => CanEdit(screenKey) ? "" : "disabled";

    public IUserPermissions AsActualUser => new UserPermissions(_resolver, _actualRole);

    public IReadOnlyList<RoleInfo> PreviewableRoles =>
        _resolver.Roles.Where(r => r.IsActive).OrderBy(r => -r.Rank).ThenBy(r => r.DisplayName).ToList();
}
