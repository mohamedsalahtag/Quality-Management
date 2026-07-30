namespace SharbatlyQMS.Web.Models.Security;

/// <summary>
/// The six roles that ship with the product, as they exist in
/// <c>portal.Role</c>. These are used for SEEDING the permission catalogue and
/// for the administrator lockout floor — <b>never</b> for deciding access. Every
/// access decision goes through the permission tables, so that an administrator
/// can compose roles the product has never heard of.
/// </summary>
public static class RoleCodes
{
    public const string Admin        = "QcAdmin";
    public const string Manager      = "QcManager";
    public const string ClaimManager = "QcClaimManager";
    public const string Supervisor   = "QcSupervisor";
    public const string Operator     = "QcOperator";
    public const string Viewer       = "QcViewer";

    /// <summary>Highest rank first, matching <c>qms_role.rank</c>.</summary>
    public static readonly string[] BuiltIn =
        { Admin, Manager, ClaimManager, Supervisor, Operator, Viewer };

    /// <summary>Every custom role code must start with this, or it would collide
    /// with the SCM application's roles in the shared <c>portal.Role</c> table.
    /// Reusing a bare name like "Operator" would hand every SCM production
    /// operator access to quality inspections.</summary>
    public const string Prefix = "Qc";

    public static bool IsBuiltIn(string? roleCode) =>
        roleCode != null && Array.Exists(BuiltIn, r => string.Equals(r, roleCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Which built-in roles a permission is granted to the first time it is
/// discovered. This is how "nobody's access changes on day one" is made true by
/// construction rather than by a hand-written seed script: each action declares
/// the audience it already had, and the catalogue materialises exactly that.
///
/// Seeds reflect <b>what the buttons showed</b> before the migration, which is
/// occasionally narrower than what the controller allowed.
/// </summary>
public enum Seed
{
    /// <summary>Granted to nobody. The administrator must grant it explicitly.</summary>
    None = 0,
    /// <summary>Every built-in role, including Viewer.</summary>
    Everyone,
    OperatorOrAbove,
    SupervisorOrAbove,
    ManagerOrAdmin,
    ClaimManagerOrAdmin,
    /// <summary>Manager, Claim Manager and Admin — the claim note composer.</summary>
    ManagerOrClaimManagerOrAdmin,
    AdminOnly,
}

public static class Seeds
{
    public static IReadOnlyList<string> RolesFor(Seed seed) => seed switch
    {
        Seed.Everyone => RoleCodes.BuiltIn,
        Seed.OperatorOrAbove =>
            new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Supervisor, RoleCodes.Operator },
        Seed.SupervisorOrAbove =>
            new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Supervisor },
        Seed.ManagerOrAdmin =>
            new[] { RoleCodes.Admin, RoleCodes.Manager },
        Seed.ClaimManagerOrAdmin =>
            new[] { RoleCodes.Admin, RoleCodes.ClaimManager },
        Seed.ManagerOrClaimManagerOrAdmin =>
            new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.ClaimManager },
        Seed.AdminOnly =>
            new[] { RoleCodes.Admin },
        _ => Array.Empty<string>()
    };

    /// <summary>Comma-separated form stored in <c>qms_permission.seed_roles</c>,
    /// so the day-one grant set stays reproducible by M14R.</summary>
    public static string? ToCsv(Seed seed)
    {
        var roles = RolesFor(seed);
        return roles.Count == 0 ? null : string.Join(',', roles);
    }
}
