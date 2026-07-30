namespace SharbatlyQMS.Web.Models.Security;

/// <summary>
/// How much of a permission a role holds. Stored as a tinyint in
/// <c>qms_role_permission.access_level</c>; <see cref="None"/> is never stored —
/// it is the absence of a row.
/// </summary>
public enum AccessLevel : byte
{
    /// <summary>No grant row exists. Deny.</summary>
    None = 0,
    /// <summary>May open the screen and read it, but perform no action on it.</summary>
    Read = 1,
    /// <summary>Full access to whatever the permission covers.</summary>
    Edit = 2,
}

public static class AccessLevels
{
    /// <summary>Parses the stored tinyint, treating anything unexpected as None
    /// so a corrupt row denies rather than grants.</summary>
    public static AccessLevel FromDb(byte value) => value switch
    {
        1 => AccessLevel.Read,
        2 => AccessLevel.Edit,
        _ => AccessLevel.None
    };

    public static string Label(AccessLevel level) => level switch
    {
        AccessLevel.Read => "Read only",
        AccessLevel.Edit => "Edit",
        _                => "No access"
    };
}
