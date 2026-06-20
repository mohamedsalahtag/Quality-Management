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
    /// <summary>SAP plant code (e.g. "RD01"). Operator-only restriction;
    /// null means the user sees every plant. See AuthPolicies.</summary>
    public string?   PlantCode      { get; set; }
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
    public const string Supervisor   = "Supervisor";
    public const string Manager      = "Manager";
    public const string ClaimManager = "ClaimManager";
    public const string SiteAdmin    = "SiteAdmin";

    // Hierarchy (2026-06-20): Viewer < Operator < Supervisor < Manager < SiteAdmin.
    // ClaimManager is a peer role parallel to Manager, scoped to the Claim
    // Management module; it does NOT sit on the main hierarchy axis.
    // Supervisor reviews operator work in the QO workflow: can Finish a
    // Submitted QO, cancel-submit it back to Open for operator fixes, and
    // edit Completed arrivals. Cannot reopen Finished QOs (Manager-only).
    public static readonly string[] All = { Viewer, Operator, Supervisor, Manager, ClaimManager, SiteAdmin };

    public static bool IsValid(string role) => Array.IndexOf(All, role) >= 0;
}

public static class AuthPolicies
{
    /// <summary>SiteAdmin only. Also gates the global Audit Log page + Excel export.</summary>
    public const string AdminOnly             = "AdminOnly";
    /// <summary>Manager or SiteAdmin. Reopen a Finished QO, parameter pages, destructive ops, per-record audit panels.</summary>
    public const string ManagerOrAdmin        = "ManagerOrAdmin";
    /// <summary>Supervisor, Manager, or SiteAdmin. Finish a Submitted QO, cancel-submit, edit Completed arrivals, view the flat data-hub report.</summary>
    public const string SupervisorOrAbove     = "SupervisorOrAbove";
    /// <summary>Operator, Supervisor, Manager, or SiteAdmin. Operational mutations on an Open QO.</summary>
    public const string OperatorOrAbove       = "OperatorOrAbove";
    /// <summary>ClaimManager or SiteAdmin. Approve / Hold actions in Claim Management.</summary>
    public const string ClaimManagerOrAdmin   = "ClaimManagerOrAdmin";
}
