using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Everything the Security screen renders: the role list, the role currently
/// being edited, and the whole permission tree grouped into screen cards.
///
/// A synthetic cross-join of roles and permissions with a computed state per
/// cell, so it gets a real view model rather than riding on ViewBag.
/// </summary>
public sealed class SecurityVm
{
    public IReadOnlyList<RoleUsage> Roles { get; init; } = Array.Empty<RoleUsage>();

    /// <summary>The role open in the editor tab. Null when none is selected.</summary>
    public RoleUsage? Selected { get; init; }

    /// <summary>Screen cards for the editor, in menu order.</summary>
    public IReadOnlyList<ScreenCardVm> Cards { get; init; } = Array.Empty<ScreenCardVm>();

    /// <summary>Permissions discovered since the last time anybody looked —
    /// surfaced as a badge so a new button doesn't stay invisible to everyone.</summary>
    public int NewSinceReview { get; init; }

    /// <summary>Rows for the comparison grid: every permission against every role.</summary>
    public IReadOnlyList<MatrixRowVm> MatrixRows { get; init; } = Array.Empty<MatrixRowVm>();

    public bool AnyObsolete { get; init; }
    public bool ShowObsolete { get; init; }

    /// <summary>True when the signed-in user is editing the role they hold.
    /// The save is refused server-side; the screen says so up front.</summary>
    public bool EditingOwnRole { get; init; }

    public DateTimeOffset? PermissionsLoadedAt { get; init; }
}

public sealed class ScreenCardVm
{
    public string ScreenKey  { get; init; } = "";
    public string Title      { get; init; } = "";
    public string GroupName  { get; init; } = "";

    /// <summary>True on the six record screens, where a role is set to
    /// read-only or edit rather than simply on or off.</summary>
    public bool SupportsAccessLevel { get; init; }

    /// <summary>Whether the screen permission itself exists in the catalogue.
    /// The attachment functions have no screen of their own, for instance.</summary>
    public bool HasScreenPermission { get; init; }

    public AccessLevel ScreenLevel { get; init; }

    public IReadOnlyList<PermissionRowVm> Actions { get; init; } = Array.Empty<PermissionRowVm>();

    public int GrantedCount => (ScreenLevel != AccessLevel.None ? 1 : 0) + Actions.Count(a => a.Granted);
    public int TotalCount    => (HasScreenPermission ? 1 : 0) + Actions.Count;
}

public sealed class PermissionRowVm
{
    public string Code        { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool   Granted     { get; init; }
    public bool   IsObsolete  { get; init; }
    /// <summary>Rendered locked and ticked: the administrator role can never be
    /// denied the Security screen, so the cell would be a lie if it were
    /// editable.</summary>
    public bool   IsLocked    { get; init; }
}

public sealed class MatrixRowVm
{
    public string ScreenTitle { get; init; } = "";
    public string Code        { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Kind        { get; init; } = "";
    public bool   IsObsolete  { get; init; }
    /// <summary>Level per role code, in the same order as SecurityVm.Roles.</summary>
    public IReadOnlyList<AccessLevel> Levels { get; init; } = Array.Empty<AccessLevel>();
}
