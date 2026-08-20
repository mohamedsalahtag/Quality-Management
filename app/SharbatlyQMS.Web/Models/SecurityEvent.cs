using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Models;

/// <summary>
/// One thing that happened to a person's access, in reading form.
///
/// Two tables record these events and neither is complete on its own:
/// <c>qms_permission_log</c> holds the readable "which grant moved" rows but
/// only from 20 Aug 2026 onward and only for grants, roles, plants and account
/// state; <c>qms_audit_log</c> holds user creation, deletion and password
/// resets, and everything before that date, but as raw JSON. This is the merged
/// view — see <see cref="Services.SecurityLogService"/> for how the overlap
/// between them is resolved.
/// </summary>
public class SecurityEvent
{
    public DateTime ChangedAt   { get; set; }
    public string   ChangedBy   { get; set; } = "";

    /// <summary>See <see cref="SecurityCategories"/>.</summary>
    public string   Category    { get; set; } = "";

    /// <summary>"User" or "Role" — see <see cref="PermissionSubjects"/>.</summary>
    public string   SubjectType { get; set; } = "";
    /// <summary>Username or role code.</summary>
    public string   SubjectKey  { get; set; } = "";
    public string?  SubjectName { get; set; }

    /// <summary>The one-line sentence shown in the table.</summary>
    public string   Headline    { get; set; } = "";

    /// <summary>Before/after pairs, already labelled and formatted.</summary>
    public IReadOnlyList<AuditNarrator.FriendlyDiff> Details { get; set; }
        = Array.Empty<AuditNarrator.FriendlyDiff>();

    /// <summary>Permission code, plant code or setting name, when there is one.
    /// Shown as small print for anyone cross-checking the Security screen.</summary>
    public string?  ItemCode    { get; set; }

    public string?  SourceIp    { get; set; }

    /// <summary>Which table this came from, so the detail panel can offer the
    /// raw JSON only where raw JSON exists.</summary>
    public bool     FromAuditLog { get; set; }

    public long?    AuditId       { get; set; }
    public string?  OldValuesJson { get; set; }
    public string?  NewValuesJson { get; set; }

    /// <summary>Sort/dedupe key: the same event written to both tables shares an
    /// actor and a subject and lands within a second or two.</summary>
    public string CorrelationKey =>
        $"{ChangedBy.ToLowerInvariant()}|{SubjectKey.ToLowerInvariant()}";
}

/// <summary>
/// What kind of security change an event is. A superset of
/// <see cref="PermissionChangeTypes"/> — the extra members are the account
/// lifecycle events that only the audit log records.
/// </summary>
public static class SecurityCategories
{
    public const string Permission     = "Permission";
    public const string Plant          = "Plant";
    public const string RoleAssignment = "RoleAssignment";
    public const string RoleCreated    = "RoleCreated";
    public const string RoleDeleted    = "RoleDeleted";
    public const string RoleSetting    = "RoleSetting";
    public const string Account        = "Account";

    // Audit-log-only: no permission-log row is written for these.
    public const string UserCreated    = "UserCreated";
    public const string UserChanged    = "UserChanged";
    public const string UserDeleted    = "UserDeleted";
    public const string PasswordReset  = "PasswordReset";

    /// <summary>Every category, in the order the filter offers them —
    /// account lifecycle first, then access, then roles.</summary>
    public static readonly string[] All =
    {
        UserCreated, UserChanged, UserDeleted, PasswordReset, Account,
        RoleAssignment, Permission, Plant,
        RoleCreated, RoleSetting, RoleDeleted
    };

    /// <summary>The ones sourced from qms_permission_log.</summary>
    public static readonly string[] FromPermissionLog =
    {
        Permission, Plant, RoleAssignment, RoleCreated, RoleDeleted, RoleSetting, Account
    };

    public static bool IsAuditOnly(string? c) =>
        c is UserCreated or UserChanged or UserDeleted or PasswordReset;

    public static string Label(string? c) => c switch
    {
        Permission     => "Permission",
        Plant          => "Plant access",
        RoleAssignment => "Role assignment",
        RoleCreated    => "Role created",
        RoleDeleted    => "Role deleted",
        RoleSetting    => "Role setting",
        Account        => "Account state",
        UserCreated    => "User created",
        UserChanged    => "User changed",
        UserDeleted    => "User deleted",
        PasswordReset  => "Password reset",
        _              => c ?? ""
    };

    public static string BadgeCss(string? c) => c switch
    {
        Permission     => "bg-primary",
        Plant          => "bg-info text-dark",
        RoleAssignment => "bg-warning text-dark",
        RoleCreated    => "bg-success",
        RoleDeleted    => "bg-danger",
        RoleSetting    => "bg-secondary",
        Account        => "bg-dark",
        UserCreated    => "bg-success",
        UserChanged    => "bg-warning text-dark",
        UserDeleted    => "bg-danger",
        PasswordReset  => "bg-danger-subtle text-danger-emphasis border border-danger-subtle",
        _              => "bg-secondary"
    };

    public static string Icon(string? c) => c switch
    {
        Permission     => "bi-key",
        Plant          => "bi-geo-alt",
        RoleAssignment => "bi-person-badge",
        RoleCreated    => "bi-plus-square",
        RoleDeleted    => "bi-trash",
        RoleSetting    => "bi-sliders",
        Account        => "bi-toggle-on",
        UserCreated    => "bi-person-plus",
        UserChanged    => "bi-person-gear",
        UserDeleted    => "bi-person-x",
        PasswordReset  => "bi-shield-lock",
        _              => "bi-dot"
    };
}

/// <summary>
/// Filters for the Users &amp; security tab. Dates are the reader's LOCAL days;
/// the service converts them to UTC.
/// </summary>
public class SecurityLogFilter
{
    public string?   Actor       { get; set; }
    public string?   SubjectKey  { get; set; }
    /// <summary>"User" or "Role".</summary>
    public string?   SubjectType { get; set; }
    public string?   Category    { get; set; }
    public string?   Search      { get; set; }
    public DateTime? From        { get; set; }
    public DateTime? To          { get; set; }
    public int       PageSize    { get; set; } = 200;

    public bool Any =>
        !string.IsNullOrWhiteSpace(Actor)       || !string.IsNullOrWhiteSpace(SubjectKey)
        || !string.IsNullOrWhiteSpace(SubjectType) || !string.IsNullOrWhiteSpace(Category)
        || !string.IsNullOrWhiteSpace(Search)   || From.HasValue || To.HasValue;

    public int ActiveCount =>
        (string.IsNullOrWhiteSpace(Actor)       ? 0 : 1) +
        (string.IsNullOrWhiteSpace(SubjectKey)  ? 0 : 1) +
        (string.IsNullOrWhiteSpace(SubjectType) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(Category)    ? 0 : 1) +
        (string.IsNullOrWhiteSpace(Search)      ? 0 : 1) +
        (From.HasValue ? 1 : 0) + (To.HasValue ? 1 : 0);
}

/// <summary>Dropdown contents for the Users &amp; security filter panel.</summary>
public class SecurityLogOptions
{
    public IReadOnlyList<string>               Actors     { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PermissionLogSubject> Subjects   { get; init; } = Array.Empty<PermissionLogSubject>();
    public IReadOnlyList<string>               Categories { get; init; } = Array.Empty<string>();
}
