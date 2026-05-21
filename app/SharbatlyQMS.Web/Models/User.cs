namespace SharbatlyQMS.Web.Models;

public class User
{
    public int       UserId         { get; set; }
    public string?   EmployeeId     { get; set; }
    public string    Username       { get; set; } = "";
    public string    FullName       { get; set; } = "";
    public string?   Email          { get; set; }
    public string?   Department     { get; set; }
    public string?   ProfilePicture { get; set; }
    public string    PasswordHash   { get; set; } = "";
    public string    Role           { get; set; } = UserRoles.Viewer;
    public bool      IsActive       { get; set; } = true;
    public bool      IsOnline       { get; set; }
    public DateTime? LastLogin      { get; set; }
    public DateTime? LastSeen       { get; set; }
    public DateTime  CreatedAt      { get; set; }
    public int?      CreatedBy      { get; set; }
    public DateTime? DisabledAt     { get; set; }
    public int?      DisabledBy     { get; set; }
}

public static class UserRoles
{
    public const string Viewer       = "Viewer";
    public const string Operator     = "Operator";
    public const string Manager      = "Manager";
    public const string ClaimManager = "ClaimManager";
    public const string SiteAdmin    = "SiteAdmin";

    // Ordered low-to-high so a future "rank" check can compare positions.
    // ClaimManager sits between Manager and SiteAdmin in the array purely
    // for cosmetic ordering -- it is NOT a hierarchical "more powerful
    // than Manager" claim. The two are peer roles with disjoint authority
    // (Manager = QC inspection; ClaimManager = commercial claim approval).
    public static readonly string[] All = { Viewer, Operator, Manager, ClaimManager, SiteAdmin };

    public static bool IsValid(string role) => Array.IndexOf(All, role) >= 0;
}

public static class AuthPolicies
{
    /// <summary>SiteAdmin only.</summary>
    public const string AdminOnly             = "AdminOnly";
    /// <summary>Manager or SiteAdmin. Parameters pages + destructive ops.</summary>
    public const string ManagerOrAdmin        = "ManagerOrAdmin";
    /// <summary>Operator, Manager, or SiteAdmin. Operational mutations.</summary>
    public const string OperatorOrAbove       = "OperatorOrAbove";
    /// <summary>ClaimManager or SiteAdmin. Approve / Hold actions in Claim Management.</summary>
    public const string ClaimManagerOrAdmin   = "ClaimManagerOrAdmin";
}
