namespace SharbatlyQMS.Web.Models;

public class QualityOrder
{
    public long      QualityOrderId { get; set; }
    public string    QualityOrderNo { get; set; } = "";
    public long      ArrivalId      { get; set; }
    public string    StatusCode     { get; set; } = "Initial";
    public DateTime? OpenedAt       { get; set; }
    public string?   OpenedBy       { get; set; }
    public DateTime? ClosedAt       { get; set; }
    public string?   ClosedBy       { get; set; }
    public string?   CloseReason    { get; set; }
    public DateTime? ReopenedAt     { get; set; }
    public string?   ReopenedBy     { get; set; }
    public string?   ReopenReason   { get; set; }
    public DateTime  CreatedAt      { get; set; }
    public string    CreatedBy      { get; set; } = "";

    // Joined from qms_arrival so the QO list can show shipment identity.
    public string?  ContainerNo   { get; set; }
    public string?  BolNo         { get; set; }
    public string?  Ebeln         { get; set; }   // SAP PO number
    public string?  VendorName    { get; set; }
    /// <summary>SAP plant inherited from the parent arrival (qms_arrival.plant).</summary>
    public string?  Plant         { get; set; }
    public string?  ArrivalNo     { get; set; }

    // Materials rolled up for the QO list "quick peek" hover (mirrors the
    // Pending Containers page). Populated only by ListAsync; empty elsewhere.
    public int                 LineCount     { get; set; }
    public List<QoMaterialLine> MaterialLines { get; set; } = new();
}

/// <summary>
/// Lightweight material line for the QO list "quick peek" tooltip — just the
/// four fields the hover shows (code, description, quantity, UoM), not the full
/// <see cref="QualityOrderMaterial"/> which carries ~24 columns per row.
/// </summary>
public class QoMaterialLine
{
    public long     QualityOrderId { get; set; }
    public string   MaterialNo     { get; set; } = "";
    public string?  MaterialDesc   { get; set; }
    public decimal? Quantity       { get; set; }
    public string?  Uom            { get; set; }
}

public class QualityOrderMaterial
{
    public long     QoMaterialId        { get; set; }
    public long     QualityOrderId      { get; set; }
    public long     ArrivalItemId       { get; set; }
    public string   MaterialNo          { get; set; } = "";
    public string?  MaterialDesc        { get; set; }
    public string?  Origin              { get; set; }
    public string?  Variety             { get; set; }
    public string?  MaterialClass       { get; set; }
    public decimal? NetWeight           { get; set; }
    public string?  MaterialSize        { get; set; }
    public string?  MaterialGroup       { get; set; }
    public string?  MaterialGroupDesc   { get; set; }
    public string?  MajorCategory       { get; set; }
    public string?  SubMajorCategory    { get; set; }   // enriched from MARA at view-time; not persisted in qms_quality_order_material yet
    public string?  Brand               { get; set; }
    public string?  PackType            { get; set; }
    public string?  PackCode            { get; set; }   // enriched from MARA at view-time
    // Joined from qms_arrival_item via arrival_item_id -- the GR/PO line
    // quantity + UoM. Used by the QO PDF Material Table; surfaces the
    // shipment quantity that previously rendered blank (2026-05-25 fix).
    public decimal? Quantity            { get; set; }
    public string?  Uom                 { get; set; }
    public bool     SizeOverridden      { get; set; }
    public string?  OriginalMaterialSize{ get; set; }
    public string?  OverrideMaterialSize{ get; set; }
    public string?  OverrideReason      { get; set; }
    public string?  OverrideApprovedBy  { get; set; }
    public DateTime?OverrideApprovedAt  { get; set; }
    // Material-level sample size (source of truth). Samples inherit it; the
    // per-sample qms_sample.sample_size is kept as an inherited cache that
    // every defect-% calculation still divides by. Set via the Override Size
    // modal (2026-06-20) when the operator wants to deviate from MARA; left
    // null otherwise so EffectiveSampleSize parses MARA's MaterialSize string.
    public short?   SampleSize          { get; set; }

    // Effective numeric size used as the inherited default on every sample
    // form. Prefers the explicit numeric column when set (e.g., after an
    // override); otherwise tries to parse MARA's MaterialSize text (e.g., "30").
    // Returns null when neither yields a positive short -- defect % then
    // shows "—" on the sample form. Read-only (no setter) so Dapper ignores
    // it during model mapping.
    public short?   EffectiveSampleSize =>
        SampleSize ?? (short.TryParse(MaterialSize, out var n) && n > 0 ? (short?)n : null);
}

public static class QualityOrderStatus
{
    public const string Initial   = "Initial";
    public const string Open      = "Open";
    public const string Submitted = "Submitted";
    // 'Closed' is the persisted code; UI labels it as "Finished" via DisplayName.
    // Kept as-is in the DB so existing Closed rows + claim queries that match on
    // status_code = 'Closed' don't need a data migration.
    public const string Closed    = "Closed";
    public const string Reopened  = "Reopened";
    public const string Cancelled = "Cancelled";

    /// <summary>Human-friendly label for views, dropdowns, badges. UI says
    /// "Finished" where the schema says "Closed".</summary>
    public static string DisplayName(string? code) => code switch
    {
        Closed   => "Finished",
        Reopened => "Open",      // transient — actual DB value is 'Open' after a reopen
        _        => code ?? ""
    };
}

public class Sample
{
    public long      SampleId           { get; set; }
    public long      QualityOrderId     { get; set; }
    public long      QoMaterialId       { get; set; }
    public int       SampleNo           { get; set; }
    public short?    CartonCount        { get; set; }
    public string?   CartonIdentifier   { get; set; }
    public string    SampleScope        { get; set; } = "OneCarton";
    public short?    SampleSize         { get; set; }
    public bool      SizeOverridden     { get; set; }
    public string?   Grower             { get; set; }
    public string?   PalletNo           { get; set; }
    public string?   GrowerPallet       { get; set; }
    public string?   PackCode           { get; set; }
    public string?   DateCode           { get; set; }
    public string?   LabelValue         { get; set; }
    public string?   LotNo              { get; set; }
    public string?   PackagingMaterial  { get; set; }
    public DateTime  CreatedAt          { get; set; }
    public string    CreatedBy          { get; set; } = "";
    public DateTime? UpdatedAt          { get; set; }
    public string?   UpdatedBy          { get; set; }
}

public class SampleReading
{
    public long     ReadingId        { get; set; }
    public long     SampleId         { get; set; }
    public string   ReadingTypeCode  { get; set; } = "";
    public decimal? NumericValue     { get; set; }
    public string?  TextValue        { get; set; }
    public string?  UnitCode         { get; set; }
    public bool?    IsWithinSpec     { get; set; }
    public int      ReadingSequence  { get; set; }
    public string   ReadingName      { get; set; } = "";  // joined from qms_reading_type
    public string   ValueKind        { get; set; } = "Numeric";
}

public class SampleDefect
{
    public long     SampleDefectId    { get; set; }
    public long     SampleId          { get; set; }
    public int      DefectId          { get; set; }
    public decimal? DefectValue       { get; set; }
    public decimal? DefectPercentage  { get; set; }
    public string?  SeverityCode      { get; set; }
    public string?  Comment           { get; set; }
    public bool?    IsWithinTolerance { get; set; }
    public string   DefectCode        { get; set; } = "";   // joined
    public string   DefectName        { get; set; } = "";   // joined
    public string   DefectCategory    { get; set; } = "";   // joined
    public string   DisplaySection    { get; set; } = "Minor"; // joined from material_group_defect
}

public class DefectCatalogEntry
{
    public int    DefectId       { get; set; }
    public string DefectCode     { get; set; } = "";
    public string DefectName     { get; set; } = "";
    public string DefectCategory { get; set; } = "Minor";
    public bool   IsActive       { get; set; }
    public int    SortOrder      { get; set; }
    /// <summary>Material group this defect belongs to (e.g. FRSH-APP). Same defect_code can repeat across groups.</summary>
    public string MaterialGroup  { get; set; } = "";
    /// <summary>Number = integer steps only; Decimal = allow fractional values on the sample form.</summary>
    public string ValueType      { get; set; } = "Number";
    /// <summary>True when at least one sample defect references this catalog row -- delete is then blocked.</summary>
    public bool   IsInUse        { get; set; }
}

// -------------- Defect category master (V22+) --------------------------
// Admin-managed list of defect categories (Major, Minor, Critical,
// Progressive, ...). Each defect references one by name; each renders as
// its own coloured section in the sample form and PDF, ordered by
// SortOrder. Replaces the old hardcoded Major/Minor two-bucket scheme.
public class DefectCategory
{
    public int     CategoryId   { get; set; }
    public string  CategoryName { get; set; } = "";
    public int     SortOrder    { get; set; }
    public string? ColorHex     { get; set; }   // '#RRGGBB'; null => neutral/fallback
    public bool    IsActive     { get; set; }
    /// <summary>True when at least one defect references this category -- delete is then blocked.</summary>
    public bool    IsInUse      { get; set; }
}

// -------------- Sample header field catalog (V20+) ---------------------
// User-input identification fields per sample. Always global (no
// material_group binding) -- a field defined here applies to every
// sample regardless of fruit. Replaces the hardcoded columns
// CartonCount / Grower / PalletNo / DateCode / etc. on qms_sample.
// sample_size stays on qms_sample (used as the denominator for defect
// percentages) and is intentionally NOT part of this catalog.

public class SampleHeaderField
{
    public int    FieldId      { get; set; }
    public string FieldCode    { get; set; } = "";
    public string FieldName    { get; set; } = "";
    /// <summary>Text | Numeric | Date. Drives the form input type and
    /// which column on qms_sample_header_value carries the value.</summary>
    public string ValueKind    { get; set; } = "Text";
    public string? DefaultUnit { get; set; }
    public bool   IsActive     { get; set; }
    public bool   IsMandatory  { get; set; }
    public int    SortOrder    { get; set; }
    /// <summary>Sample | Material. 'Material'-scoped fields are entered once
    /// per qms_quality_order_material (on the Material details panel) and every
    /// sample inherits them; 'Sample'-scoped fields are entered per sample.</summary>
    public string Scope        { get; set; } = "Sample";
    /// <summary>True when at least one qms_sample_header_value row
    /// references this field -- blocks delete; admin should mark
    /// inactive instead.</summary>
    public bool   IsInUse      { get; set; }
}

public class SampleHeaderValue
{
    public long      SampleId     { get; set; }
    public int       FieldId      { get; set; }
    public string?   TextValue    { get; set; }
    public decimal?  NumericValue { get; set; }
    public DateTime? DateValue    { get; set; }
    // Joined from qms_sample_header_field for convenience.
    public string    FieldCode    { get; set; } = "";
    public string    FieldName    { get; set; } = "";
    public string    ValueKind    { get; set; } = "Text";
    public string?   DefaultUnit  { get; set; }
    public int       SortOrder    { get; set; }
}

/// <summary>Material-level header value (qms_qo_material_header_value), keyed by
/// qo_material_id. Mirrors <see cref="SampleHeaderValue"/> for the fields that
/// are entered once per material and inherited by every sample.</summary>
public class MaterialHeaderValue
{
    public long      QoMaterialId { get; set; }
    public int       FieldId      { get; set; }
    public string?   TextValue    { get; set; }
    public decimal?  NumericValue { get; set; }
    public DateTime? DateValue    { get; set; }
    // Joined from qms_sample_header_field for convenience.
    public string    FieldCode    { get; set; } = "";
    public string    FieldName    { get; set; } = "";
    public string    ValueKind    { get; set; } = "Text";
    public string?   DefaultUnit  { get; set; }
    public int       SortOrder    { get; set; }
}

public class ReadingTypeEntry
{
    public int    ReadingTypeId   { get; set; }
    public string ReadingTypeCode { get; set; } = "";
    public string ReadingName     { get; set; } = "";
    public string ValueKind       { get; set; } = "Numeric";
    public string? DefaultUnit    { get; set; }
    public bool   IsActive        { get; set; }
    public int    SortOrder       { get; set; }
    /// <summary>Material group this reading belongs to (e.g. APPLE). Same code can repeat across groups.</summary>
    public string MaterialGroup   { get; set; } = "";
    /// <summary>When true the sample form refuses to save until this reading has a value.</summary>
    public bool   IsMandatory     { get; set; }
    /// <summary>True when at least one sample reading uses this code on a sample from this material group -- delete blocked.</summary>
    public bool   IsInUse         { get; set; }
    /// <summary>How this reading is aggregated in the QO PDF grouped summary:
    /// text | count | sum | sum_over_size | formula. Backfilled in V18 from value_kind.</summary>
    public string DisplayMode     { get; set; } = "sum";
}

// ---------------- Quality Order grouped summary ------------------------
// Used by the Quality Order PDF page 1 to roll up every material that
// shares (MaterialGroup, Brand, Variety, Grade) into a single block:
// totals across all the contributing samples, the full active defect
// catalog (zeros included), and aggregated readings honoring each
// reading-type's display_mode.

public class MaterialGroupSummary
{
    public string  MaterialGroup    { get; set; } = "";   // e.g. "FRSH-APP"
    public string? MaterialGroupDesc{ get; set; }
    public string? Brand            { get; set; }
    public string? Variety          { get; set; }
    public string? Grade            { get; set; }          // = MaterialClass
    public string? MajorCategory    { get; set; }
    public int     SumSampleSize    { get; set; }          // Σ sample_size across all samples in group
    public decimal SumPoQuantity    { get; set; }          // Σ arrival_item.quantity across the group's materials (PO qty)

    // One section per defect category (ordered by category sort_order),
    // replacing the old fixed Major/Minor two-bucket scheme (V22+).
    public List<DefectCategorySection> DefectSections { get; set; } = new();

    public List<ReadingAggRow> Readings { get; set; } = new();

    public int MaterialCount { get; set; }
    public int SampleCount   { get; set; }
}

public class DefectAggRow
{
    public int     DefectId   { get; set; }
    public string  Code       { get; set; } = "";
    public string  Name       { get; set; } = "";
    public string  Category   { get; set; } = "Minor";    // raw catalog category
    public decimal SumValue   { get; set; }
    public decimal Percentage { get; set; }                // SumValue / SumSampleSize × 100 (0 if denom=0)
}

// One defect category's section within a grouped summary / sample card.
public class DefectCategorySection
{
    public string  CategoryName { get; set; } = "";
    public string? ColorHex     { get; set; }
    public int     SortOrder    { get; set; }
    public List<DefectAggRow> Rows { get; set; } = new();
    public decimal TotalPct => Rows.Sum(r => r.Percentage);
    // Σ recorded defect pieces across this category (sum of the summed
    // materials' defect values), shown in the summary category header.
    public decimal TotalPieces => Rows.Sum(r => r.SumValue);
}

public class ReadingAggRow
{
    public string  Code        { get; set; } = "";
    public string  Name        { get; set; } = "";
    public string  DisplayMode { get; set; } = "";        // text|count|sum|sum_over_size|formula
    public string? Unit        { get; set; }
    /// <summary>Pre-rendered display string per DisplayMode -- the PDF prints
    /// it verbatim. Empty string for 'formula' rows until the formula
    /// language is designed.</summary>
    public string  DisplayValue{ get; set; } = "";
}
