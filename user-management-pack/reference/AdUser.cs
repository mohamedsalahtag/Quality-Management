// AD user record returned from LDAP queries. Used by AdService and the
// AD Browse / Mass Create admin pages. The "registration status" fields
// (IsRegistered, UserId, SystemRole, SystemIsActive) are populated by the
// DB-side merge step after the LDAP fetch.

namespace HelpDesk.Models;

public class AdUser
{
    public string Username       { get; set; } = "";       // sAMAccountName
    public string FullName       { get; set; } = "";       // displayName / cn
    public string Email          { get; set; } = "";       // mail
    public string Department     { get; set; } = "";       // department
    public string EmployeeId     { get; set; } = "";
    public bool   IsActive       { get; set; }              // userAccountControl says enabled

    // Registration status — filled in after we cross-reference with the local DB
    public bool   IsRegistered   { get; set; }
    public int?   UserId         { get; set; }
    public string? SystemRole    { get; set; }
    public bool   SystemIsActive { get; set; }
}
