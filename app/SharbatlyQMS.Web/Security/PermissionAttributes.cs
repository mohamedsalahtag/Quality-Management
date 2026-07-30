using Microsoft.AspNetCore.Authorization;
using SharbatlyQMS.Web.Models.Security;

namespace SharbatlyQMS.Web.Security;

/// <summary>
/// Marker every controller action must carry exactly one of. A startup check —
/// mirrored by a unit test so it fails the build rather than the deployment —
/// refuses to start if any action is missing one. That is what makes "any
/// future addition is also a permission" true rather than aspirational.
/// </summary>
public interface IPermissionDecision
{
    /// <summary>The permission a request must hold, or null when the action is
    /// deliberately outside the permission system.</summary>
    string? PermissionCode { get; }
}

/// <summary>
/// Requires a named ACTION permission — one button or function.
///
/// Implements <c>IAuthorizationRequirementData</c> so each attribute carries its
/// own requirement: no <c>AddPolicy</c> call per permission and no custom policy
/// provider. Note that <see cref="GetRequirements"/> runs without dependency
/// injection, which is why the owning screen is derived from the code rather
/// than looked up.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute, IAuthorizationRequirementData, IPermissionDecision
{
    public RequirePermissionAttribute(string code, Seed seedFor, string displayName)
    {
        Code        = code;
        SeedFor     = seedFor;
        DisplayName = displayName;
    }

    public string Code        { get; }
    public Seed   SeedFor     { get; }
    public string DisplayName { get; }

    /// <summary>
    /// True when the action only READS. On the six levelled screens an action
    /// normally also requires the screen to be at Edit; a read-only action
    /// (downloading the PDF, exporting) needs the screen at Read instead.
    /// Without this, setting a screen to Read-only would take away its reports.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>Ordering within its screen card on the Security screen.</summary>
    public int SortOrder { get; init; } = 500;

    public string? PermissionCode => Code;

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return new PermissionRequirement(Code, ReadOnly ? AccessLevel.Read : AccessLevel.Edit, isScreen: false);
    }
}

/// <summary>
/// Requires access to a SCREEN. Used for the page itself and for the AJAX
/// partials that render pieces of it — those inherit the screen rather than
/// getting a permission of their own, because "SamplePhotosPanel" means nothing
/// to an administrator composing a role.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequireScreenAttribute : AuthorizeAttribute, IAuthorizationRequirementData, IPermissionDecision
{
    public RequireScreenAttribute(string screenKey, Seed seedFor, string displayName)
    {
        ScreenKey   = screenKey;
        SeedFor     = seedFor;
        DisplayName = displayName;
    }

    /// <summary>Overload for the partial renderers, which inherit a screen that
    /// another action already declares. Their seed and label come from that
    /// declaration, so repeating them here would be a second source of truth.</summary>
    public RequireScreenAttribute(string screenKey)
    {
        ScreenKey = screenKey;
        SeedFor   = Seed.None;
        Inherited = true;
    }

    public string  ScreenKey   { get; }
    public Seed    SeedFor     { get; }
    public string? DisplayName { get; }

    /// <summary>True when this attribute is riding on a screen another action
    /// declares; the catalogue must not try to register it a second time.</summary>
    public bool Inherited { get; }

    /// <summary>Require Edit on the screen rather than merely Read.</summary>
    public AccessLevel Level { get; init; } = AccessLevel.Read;

    public int SortOrder { get; init; } = 10;

    public string? PermissionCode => ScreenKey;

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return new PermissionRequirement(ScreenKey, Level, isScreen: true);
    }
}

/// <summary>
/// Outside the permission system on purpose: self-service that must never be
/// revocable. Signing out, viewing and editing your own profile, and clearing an
/// impersonation. An administrator who could revoke these could trap a user in
/// the application with no way out.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class QmsAlwaysAllowedAttribute : Attribute, IPermissionDecision
{
    public string? PermissionCode => null;
}
