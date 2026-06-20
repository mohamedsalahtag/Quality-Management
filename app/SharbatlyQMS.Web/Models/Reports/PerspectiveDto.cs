namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V34 (2026-06-20). Wire DTO returned to the analyzer UI when listing
/// or loading saved perspectives. <c>IsOwn</c> drives the "delete / set
/// default" button visibility; the server still re-checks before mutating.
/// </summary>
public class PerspectiveDto
{
    public long      Id            { get; set; }
    public string    Name          { get; set; } = "";
    public string    Scope         { get; set; } = "private";
    public bool      IsDefault     { get; set; }
    public bool      IsOwn         { get; set; }
    public string    OwnerUsername { get; set; } = "";
    public string    ConfigJson    { get; set; } = "{}";
    public DateTime  CreatedAt     { get; set; }
    public DateTime? UpdatedAt     { get; set; }
}

/// <summary>
/// V34 (2026-06-20). Save / update payload. <c>Id = null</c> means insert.
/// <c>Scope='shared'</c> is rejected server-side for non-Manager/SiteAdmin.
/// </summary>
public class PerspectiveSaveRequest
{
    public long?   Id          { get; set; }
    public string? ReportKey   { get; set; }
    public string? Name        { get; set; }
    public string? Scope       { get; set; }       // 'private' | 'shared'
    public bool    IsDefault   { get; set; }
    public string? ConfigJson  { get; set; }
}
