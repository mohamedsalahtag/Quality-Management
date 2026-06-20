namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V34 (2026-06-20). Saved configuration for the Perspective Analyzer.
/// Mirrors dbo.qms_perspective row-for-row.
/// </summary>
public class Perspective
{
    public long      PerspectiveId  { get; set; }
    public string    ReportKey      { get; set; } = "";
    public string    Name           { get; set; } = "";
    public string    OwnerUsername  { get; set; } = "";
    public string    Scope          { get; set; } = "private";   // 'private' | 'shared'
    public bool      IsDefault      { get; set; }
    public string    ConfigJson     { get; set; } = "{}";
    public DateTime  CreatedAt      { get; set; }
    public string    CreatedBy      { get; set; } = "";
    public DateTime? UpdatedAt      { get; set; }
    public string?   UpdatedBy      { get; set; }
}

public static class PerspectiveScopes
{
    public const string Private = "private";
    public const string Shared  = "shared";

    public static bool IsValid(string s) => s == Private || s == Shared;
}
