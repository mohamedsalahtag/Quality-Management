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
    /// <summary>Human-readable label, e.g. "Quality order QO-2026-000042".
    /// Resolved by AuditService.ResolveLabelsAsync — before that existed this
    /// was set by nobody, so every row rendered as a bare "#387".</summary>
    public string DisplayLabel       { get; set; } = "";

    /// <summary>QC number this entry ultimately belongs to, resolved through the
    /// sample / material / claim the row was written against. Null for entries
    /// with no quality order behind them (users, roles, settings).</summary>
    public string? QoNo              { get; set; }
    /// <summary>Container behind this entry, resolved the same way.</summary>
    public string? ContainerNo       { get; set; }

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

    /// <summary>Start of the range as the reader's LOCAL day, converted to UTC
    /// by the service. These replaced fromUtc/toUtc, which asked for UTC dates
    /// while the table rendered local times — so a row at 01:00 Riyadh appeared
    /// to fall outside a range that visibly contained it.</summary>
    public DateTime?  From         { get; set; }
    /// <summary>End of the range, inclusive, as the reader's LOCAL day.</summary>
    public DateTime?  To           { get; set; }

    /// <summary>
    /// When false (the default) the per-sample bookkeeping types are hidden —
    /// see <see cref="Services.AuditNarrator.NoisyEntityTypes"/>. They are 89%
    /// of the log, and showing them by default buries everything else.
    /// An explicit entity-type filter always wins over this.
    /// </summary>
    public bool       ShowTechnical { get; set; }
    public string[]?  EntityTypes  { get; set; }
    public string[]?  ActionCodes  { get; set; }

    // ---- Record scope -------------------------------------------------------
    // "Show me everything that happened to this container" is the question the
    // log could not answer before: an audit row names a sample id, not the QC
    // number or container a person actually knows. These four are resolved
    // through the arrival -> quality order -> sample chain in the service, so a
    // QC number also matches its samples, readings, defects and material lines.

    /// <summary>Quality-order number, partial match (e.g. "000353").</summary>
    public string?    QoNo         { get; set; }
    /// <summary>Container number, partial match.</summary>
    public string?    Container    { get; set; }
    /// <summary>Supplier / vendor name, partial match.</summary>
    public string?    Vendor       { get; set; }
    /// <summary>Plant code, exact match.</summary>
    public string?    Plant        { get; set; }

    /// <summary>True when any record-scope filter is set — the service only
    /// pays for the resolution join when one is.</summary>
    public bool HasRecordScope =>
        !string.IsNullOrWhiteSpace(QoNo)   || !string.IsNullOrWhiteSpace(Container)
        || !string.IsNullOrWhiteSpace(Vendor) || !string.IsNullOrWhiteSpace(Plant);

    /// <summary>True when the reader has narrowed anything at all. Drives the
    /// filter panel's "N active" badge and whether it opens on load.</summary>
    public bool Any =>
        HasRecordScope
        || (Users != null && Users.Any(u => !string.IsNullOrWhiteSpace(u)))
        || (EntityTypes != null && EntityTypes.Length > 0)
        || (ActionCodes != null && ActionCodes.Length > 0)
        || From.HasValue || To.HasValue || ShowTechnical;

    /// <summary>How many filters are set, for the collapsed panel's summary.</summary>
    public int ActiveCount =>
        (string.IsNullOrWhiteSpace(QoNo)      ? 0 : 1) +
        (string.IsNullOrWhiteSpace(Container) ? 0 : 1) +
        (string.IsNullOrWhiteSpace(Vendor)    ? 0 : 1) +
        (string.IsNullOrWhiteSpace(Plant)     ? 0 : 1) +
        (Users != null && Users.Any(u => !string.IsNullOrWhiteSpace(u)) ? 1 : 0) +
        (EntityTypes != null && EntityTypes.Length > 0 ? 1 : 0) +
        (ActionCodes != null && ActionCodes.Length > 0 ? 1 : 0) +
        (From.HasValue ? 1 : 0) + (To.HasValue ? 1 : 0) +
        (ShowTechnical ? 1 : 0);

    // Keyset cursor (set by the previous page link):
    public DateTime?  CursorTime   { get; set; }
    public long?      CursorId     { get; set; }
    public int        PageSize     { get; set; } = 25;
}

/// <summary>Dropdown contents for the activity-log filter panel. Sourced from
/// the data itself so an option can never come back empty.</summary>
public class AuditFilterOptions
{
    public IReadOnlyList<string> Vendors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Plants  { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Actors  { get; init; } = Array.Empty<string>();
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

    // Administrative / master-data entity types (added 2026-07-02 to close the
    // audit-coverage gap: user-role, catalog, and configuration changes that the
    // operational audit depends on were previously untracked).
    public const string User                 = "User";
    public const string Configuration        = "Configuration";
    public const string DefectCatalog        = "DefectCatalog";
    public const string DefectCategory       = "DefectCategory";
    public const string ReadingType          = "ReadingType";
    public const string SampleHeaderField    = "SampleHeaderField";
    public const string ArrivalField         = "ArrivalField";
    public const string ReportUnit           = "ReportUnit";
    public const string CodeDescription      = "CodeDescription";
    /// <summary>Role creation, deletion and permission-grant changes made on the
    /// Security screen. The audit row is the only record of who changed a grant,
    /// so it is written inside the same transaction as the change itself.</summary>
    public const string Role                 = "Role";
    public const string System               = "System";

    // Note: material-size override events are recorded under
    // QualityOrderMaterial (with action_code='Override' or 'OverrideCleared')
    // for continuity with the existing audit rows already in qms_audit_log.
    // The plan's separate "MaterialOverride" type was collapsed into this one
    // during implementation -- no behavioural difference, fewer entity types
    // to maintain.

    // Operational entity types shown as the default filter-chip group on the
    // global audit page (FR-018). Administrative types are tracked separately
    // (Admin) so the operational chip group stays focused on the inspection flow.
    public static readonly string[] All =
    {
        Arrival, ArrivalItem, ArrivalChecklist,
        QualityOrder, QualityOrderMaterial,
        Sample, SampleReading, SampleDefect,
        Claim, ClaimNote
    };

    /// <summary>Administrative / master-data entity types (user, config, catalog, purge).</summary>
    public static readonly string[] Admin =
    {
        User, Configuration, DefectCatalog, DefectCategory,
        ReadingType, SampleHeaderField, ArrivalField,
        ReportUnit, CodeDescription, Role, System
    };

    public static bool IsValid(string? type) =>
        !string.IsNullOrEmpty(type) &&
        (Array.IndexOf(All, type) >= 0 || Array.IndexOf(Admin, type) >= 0);
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

    // Administrative actions (added 2026-07-02)
    public const string PasswordReset      = "PasswordReset";
    public const string Purged             = "Purged";

    public static readonly string[] All =
    {
        Created, Updated, Deleted,
        Opened, Submitted, CancelSubmit, Closed, Reopened, Cancelled,
        ClaimRequest, PassedQC, Approved, Hold,
        Override, OverrideCleared,
        PasswordReset, Purged
    };

    /// <summary>The simple-CRUD subset for the global filter chip group (FR-010).</summary>
    public static readonly string[] CrudOnly = { Created, Updated, Deleted };

    public static bool IsValid(string? code) =>
        !string.IsNullOrEmpty(code) && Array.IndexOf(All, code) >= 0;
}
