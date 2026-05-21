// Per-group SMTP override model. One row per support group; absent or
// IsEnabled=false means the group falls back to the global SMTP settings.

namespace HelpDesk.Models;

public class GroupMailConfig
{
    public int    ConfigId      { get; set; }
    public int    GroupId       { get; set; }
    public string GroupName     { get; set; } = "";
    public string SmtpHost      { get; set; } = "";
    public int    SmtpPort      { get; set; } = 587;
    public string SmtpUser      { get; set; } = "";
    public string SmtpPassword  { get; set; } = "";
    public string SmtpFromEmail { get; set; } = "";
    public string SmtpFromName  { get; set; } = "";
    public bool   SmtpEnableSsl { get; set; } = true;
    public bool   IsEnabled     { get; set; } = false;
    public DateTime UpdatedAt   { get; set; }
}
