// User domain model + role constants.

namespace HelpDesk.Models;

public class User
{
    public int      UserId         { get; set; }
    public string   EmployeeId     { get; set; } = "";
    public string   AdUsername     { get; set; } = "";   // login key
    public string   FullName       { get; set; } = "";
    public string   Email          { get; set; } = "";
    public string?  Department     { get; set; }
    public string?  ProfilePicture { get; set; }         // /avatars/{userId}.{ext}
    public string   PasswordHash   { get; set; } = "";   // BCrypt local fallback
    public string   Role           { get; set; } = UserRoles.Requester;
    public bool     IsActive       { get; set; } = true;
    public bool     IsOnline       { get; set; }
    public DateTime? LastLogin     { get; set; }
    public DateTime? LastSeen      { get; set; }
    public DateTime  CreatedAt     { get; set; }
    public int?      CreatedBy     { get; set; }
    public DateTime? DisabledAt    { get; set; }
    public int?      DisabledBy    { get; set; }
}

public static class UserRoles
{
    public const string Requester         = "Requester";
    public const string Technician        = "Technician";
    public const string FirstLevelSupport = "FirstLevelSupport";
    public const string SiteAdmin         = "SiteAdmin";
}
