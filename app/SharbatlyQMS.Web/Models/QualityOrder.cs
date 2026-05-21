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
    public string?  ArrivalNo     { get; set; }
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
    public bool     SizeOverridden      { get; set; }
    public string?  OriginalMaterialSize{ get; set; }
    public string?  OverrideMaterialSize{ get; set; }
    public string?  OverrideReason      { get; set; }
    public string?  OverrideApprovedBy  { get; set; }
    public DateTime?OverrideApprovedAt  { get; set; }
}

public static class QualityOrderStatus
{
    public const string Initial   = "Initial";
    public const string Open      = "Open";
    public const string Closed    = "Closed";
    public const string Reopened  = "Reopened";
    public const string Cancelled = "Cancelled";
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
}
