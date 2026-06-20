namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V34 (2026-06-20). View-model for the _PerspectiveAnalyzer.cshtml partial.
/// Carries the report identity, the host page's current filter query (so the
/// pivot runs against the same scope as the preview above it), and the
/// per-request CanShare flag derived from the user's role.
/// </summary>
public class PerspectiveAnalyzerVm
{
    public string  ReportKey   { get; set; } = "";
    public string? FilterQuery { get; set; }   // raw "?qoId=..." string from Context.Request.QueryString
    public bool    CanShare    { get; set; }
}
