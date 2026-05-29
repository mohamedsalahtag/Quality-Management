using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// View model for the global audit log page (Views/Audit/Index.cshtml).
/// Carries the active filter so it can be echoed back into the form
/// inputs, the current page of rows, and the keyset cursor for the next
/// page (or null if there is no next page).
/// </summary>
public class AuditListVm
{
    public AuditFilter Filter { get; set; } = new();
    public IReadOnlyList<AuditEntryListRow> Rows { get; set; } = Array.Empty<AuditEntryListRow>();
    public DateTime? NextCursorTime { get; set; }
    public long?     NextCursorId   { get; set; }
    public bool      HasMore        { get; set; }
}
