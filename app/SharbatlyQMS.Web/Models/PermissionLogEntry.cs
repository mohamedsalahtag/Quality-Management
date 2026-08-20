namespace SharbatlyQMS.Web.Models;

/// <summary>
/// One permission change, already in the words a person would use. Written by
/// <see cref="Services.IPermissionLogService"/>; see M21 for why this exists
/// alongside the generic audit log.
/// </summary>
public class PermissionLogEntry
{
    public long      LogId       { get; set; }
    public DateTime  ChangedAt   { get; set; }
    public string    ChangedBy   { get; set; } = "";

    /// <summary>See <see cref="PermissionSubjects"/>.</summary>
    public string    SubjectType { get; set; } = "";
    /// <summary>Role code, or username.</summary>
    public string    SubjectKey  { get; set; } = "";
    /// <summary>Display name at the time of the change. Denormalised on purpose:
    /// a role renamed later must not rewrite the history of what it was called.</summary>
    public string?   SubjectName { get; set; }

    /// <summary>See <see cref="PermissionChangeTypes"/>.</summary>
    public string    ChangeType  { get; set; } = "";
    public string?   ItemCode    { get; set; }
    public string?   ItemName    { get; set; }
    public string?   OldValue    { get; set; }
    public string?   NewValue    { get; set; }
    public string?   SourceIp    { get; set; }
}

public static class PermissionSubjects
{
    public const string Role = "Role";
    public const string User = "User";
}

public static class PermissionChangeTypes
{
    /// <summary>A single permission's access level on a role changed.</summary>
    public const string Permission     = "Permission";
    /// <summary>A plant was added to or removed from a user.</summary>
    public const string Plant          = "Plant";
    /// <summary>A user's role changed (including on creation).</summary>
    public const string RoleAssignment = "RoleAssignment";
    public const string RoleCreated    = "RoleCreated";
    public const string RoleDeleted    = "RoleDeleted";
    /// <summary>Role rename, description, active flag or plant-scoping — none of
    /// which produced any audit row before M21, though deactivating a role locks
    /// out every holder.</summary>
    public const string RoleSetting    = "RoleSetting";
    /// <summary>Account enabled, disabled or deleted.</summary>
    public const string Account        = "Account";

    public static string Label(string? t) => t switch
    {
        Permission     => "Permission",
        Plant          => "Plant access",
        RoleAssignment => "Role assignment",
        RoleCreated    => "Role created",
        RoleDeleted    => "Role deleted",
        RoleSetting    => "Role setting",
        Account        => "Account",
        _              => t ?? ""
    };

    public static string BadgeCss(string? t) => t switch
    {
        Permission     => "bg-primary",
        Plant          => "bg-info text-dark",
        RoleAssignment => "bg-warning text-dark",
        RoleCreated    => "bg-success",
        RoleDeleted    => "bg-danger",
        RoleSetting    => "bg-secondary",
        Account        => "bg-dark",
        _              => "bg-secondary"
    };
}

/// <summary>Filters for the permission-log page. Dates are the user's LOCAL
/// days; the service converts them to UTC.</summary>
public class PermissionLogFilter
{
    public string?   Actor       { get; set; }
    public string?   SubjectKey  { get; set; }
    public string?   SubjectType { get; set; }
    public string?   ChangeType  { get; set; }
    public string?   Search      { get; set; }
    public DateTime? From        { get; set; }
    public DateTime? To          { get; set; }
    public int       PageSize    { get; set; } = 200;

    public bool Any =>
        !string.IsNullOrWhiteSpace(Actor)       || !string.IsNullOrWhiteSpace(SubjectKey)
        || !string.IsNullOrWhiteSpace(SubjectType) || !string.IsNullOrWhiteSpace(ChangeType)
        || !string.IsNullOrWhiteSpace(Search)   || From.HasValue || To.HasValue;
}

public sealed record PermissionLogSubject(string SubjectKey, string SubjectType, string? SubjectName);

public class PermissionLogOptions
{
    public IReadOnlyList<string>               Actors      { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PermissionLogSubject> Subjects    { get; init; } = Array.Empty<PermissionLogSubject>();
    public IReadOnlyList<string>               ChangeTypes { get; init; } = Array.Empty<string>();
}
