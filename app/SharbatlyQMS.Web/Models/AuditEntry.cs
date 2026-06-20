namespace SharbatlyQMS.Web.Models;

/// <summary>
/// One immutable row in qms_audit_log. Append-only -- never updated, never
/// deleted. Hydrated by Dapper via SELECT col AS Prop aliasing.
/// </summary>
public class AuditEntry
{
    public long      AuditId         { get; set; }
    public string    EntityType      { get; set; } = "";
    public long      EntityId        { get; set; }
    public string    ActionCode      { get; set; } = "";
    public string?   OldValuesJson   { get; set; }
    public string?   NewValuesJson   { get; set; }
    public DateTime  ChangedAt       { get; set; }
    public string    ChangedBy       { get; set; } = "";
    public string?   SourceIp         { get; set; }
    public string?   SourceUserAgent  { get; set; }
    public string?   SourceDeviceName { get; set; }
}

/// <summary>
/// Row shape returned by AuditService.ListAsync / GetForRecordAsync. Adds
/// derived fields the Razor view consumes directly (so views don't parse
/// JSON inline).
/// </summary>
public class AuditEntryListRow : AuditEntry
{
    /// <summary>Human-readable label, e.g. "Quality Order QO-2026-000042 — Closed".</summary>
    public string DisplayLabel       { get; set; } = "";

    /// <summary>Computed in C# from OldValuesJson + NewValuesJson by AuditService.ParseDiffs.</summary>
    public IReadOnlyList<DiffRow> DiffRows { get; set; } = Array.Empty<DiffRow>();
}

/// <summary>One field-level change inside an Updated entry.</summary>
public record DiffRow(string FieldName, string? OldValue, string? NewValue);

/// <summary>
/// Stateless URL-binding for the global audit log page. Defaults make
/// "no filter" mean "newest 25 across all types and actions, descending".
/// </summary>
public class AuditFilter
{
    public string[]?  Users        { get; set; }
    public DateTime?  FromUtc      { get; set; }
    public DateTime?  ToUtc        { get; set; }
    public string[]?  EntityTypes  { get; set; }
    public string[]?  ActionCodes  { get; set; }
    // Keyset cursor (set by the previous page link):
    public DateTime?  CursorTime   { get; set; }
    public long?      CursorId     { get; set; }
    public int        PageSize     { get; set; } = 25;
}

/// <summary>
/// The eleven tracked operational entity types (FR-018). The list is a
/// C# constant rather than a SQL CHECK so future entity types can be
/// added without another migration.
/// </summary>
public static class EntityTypes
{
    public const string Arrival              = "Arrival";
    public const string ArrivalItem          = "ArrivalItem";
    public const string ArrivalChecklist     = "ArrivalChecklist";
    public const string QualityOrder         = "QualityOrder";
    public const string QualityOrderMaterial = "QualityOrderMaterial";
    public const string Sample               = "Sample";
    public const string SampleReading        = "SampleReading";
    public const string SampleDefect         = "SampleDefect";
    public const string Claim                = "Claim";
    public const string ClaimNote            = "ClaimNote";

    // Note: material-size override events are recorded under
    // QualityOrderMaterial (with action_code='Override' or 'OverrideCleared')
    // for continuity with the existing audit rows already in qms_audit_log.
    // The plan's separate "MaterialOverride" type was collapsed into this one
    // during implementation -- no behavioural difference, fewer entity types
    // to maintain.

    public static readonly string[] All =
    {
        Arrival, ArrivalItem, ArrivalChecklist,
        QualityOrder, QualityOrderMaterial,
        Sample, SampleReading, SampleDefect,
        Claim, ClaimNote
    };

    public static bool IsValid(string? type) =>
        !string.IsNullOrEmpty(type) && Array.IndexOf(All, type) >= 0;
}

/// <summary>
/// Audit action codes. Generic CRUD (Created/Updated/Deleted) plus the
/// domain-specific labels preserved from existing services per the
/// 2026-05-20 clarification on domain action preservation.
/// </summary>
public static class ActionCodes
{
    // Generic CRUD
    public const string Created            = "Created";
    public const string Updated            = "Updated";
    public const string Deleted            = "Deleted";

    // QualityOrder transitions
    public const string Opened             = "Opened";
    public const string Submitted          = "Submitted";       // V31: operator marks data entry complete
    public const string CancelSubmit       = "CancelSubmit";    // V31: supervisor reverts Submitted -> Open
    public const string Closed             = "Closed";          // UI-labelled "Finished"
    public const string Reopened           = "Reopened";
    public const string Cancelled          = "Cancelled";

    // Claim transitions
    public const string ClaimRequest       = "ClaimRequest";
    public const string PassedQC           = "PassedQC";
    public const string Approved           = "Approved";
    public const string Hold               = "Hold";

    // Material size overrides
    public const string Override           = "Override";
    public const string OverrideCleared    = "OverrideCleared";

    public static readonly string[] All =
    {
        Created, Updated, Deleted,
        Opened, Submitted, CancelSubmit, Closed, Reopened, Cancelled,
        ClaimRequest, PassedQC, Approved, Hold,
        Override, OverrideCleared
    };

    /// <summary>The simple-CRUD subset for the global filter chip group (FR-010).</summary>
    public static readonly string[] CrudOnly = { Created, Updated, Deleted };

    public static bool IsValid(string? code) =>
        !string.IsNullOrEmpty(code) && Array.IndexOf(All, code) >= 0;
}
