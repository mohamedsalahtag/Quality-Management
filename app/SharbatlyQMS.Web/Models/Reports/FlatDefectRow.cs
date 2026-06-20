namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V31 (2026-06-20). One row per (sample × catalog-defect) for the data-hub
/// report at /Reports/FlatDefects. Used by the in-browser preview table and
/// by the Excel export. Designed for pivot-table analysis: every catalog
/// defect on every sample appears, with <see cref="DefectValue"/> = 0 when
/// the operator left it blank.
///
/// Static columns first (PO/material/sample), then dynamic value columns
/// (defect details), then loose key→value bags for the configurable header
/// fields and reading types. Excel projection flattens the bags into wide
/// columns at write-time.
/// </summary>
public class FlatDefectRow
{
    // -------------------- Arrival / PO context --------------------
    public string?   ArrivalNo       { get; set; }
    public string?   Plant           { get; set; }
    public string?   StorageLocation { get; set; }   // V31: per-line storage loc from qms_arrival_item
    public string?   ContainerNo     { get; set; }
    public string?   BolNo           { get; set; }
    public string?   Ebeln           { get; set; }   // PO (current mapping = ShipmentNo)
    public string?   Sto             { get; set; }
    public DateTime? PoDate          { get; set; }   // V31: cc.doc_date
    public DateTime? LoadingDate     { get; set; }   // V31: ss.loading_date (not shown by default)
    public DateTime? ShippingDate    { get; set; }   // V31: ss.sailing_date
    public DateTime? ArrivalDate     { get; set; }
    public DateTime? ReceiveDate     { get; set; }
    public short?    TransitDays     { get; set; }   // V31: ss.transit_days
    public string?   VendorName      { get; set; }
    public string?   VendorNo        { get; set; }   // V31: a.vendor_no

    // -------------------- QO context --------------------
    public long      QualityOrderId  { get; set; }
    public string?   QualityOrderNo  { get; set; }
    public string?   QoStatus        { get; set; }   // raw status code (Initial / Open / Submitted / Closed / Cancelled)
    public string?   QoStatusDisplay { get; set; }   // UI label (Closed -> Finished)
    public DateTime? QoCreatedAt     { get; set; }

    // -------------------- Material context (incl. MARA snapshot) --------------------
    public long      QoMaterialId        { get; set; }
    public string?   MaterialNo          { get; set; }
    public string?   MaterialDesc        { get; set; }
    public string?   MaterialGroup       { get; set; }
    public string?   MaterialGroupDesc   { get; set; }
    public string?   MajorCategory       { get; set; }
    public string?   SubMajorCategory    { get; set; }
    public string?   Variety             { get; set; }
    public string?   MaterialClass       { get; set; }
    public string?   Origin              { get; set; }
    public string?   Brand               { get; set; }
    public string?   PackType            { get; set; }
    public string?   PackCode            { get; set; }
    public decimal?  NetWeight           { get; set; }
    public decimal?  ArrivalItemQuantity { get; set; }
    public string?   ArrivalItemUom      { get; set; }
    public string?   MaterialSize        { get; set; }
    public short?    MaterialSampleSize  { get; set; }

    // -------------------- Sample context --------------------
    public long      SampleId         { get; set; }
    public int       SampleNo         { get; set; }
    public string?   SampleScope      { get; set; }
    public short?    SampleSize       { get; set; }
    public bool      SizeOverridden   { get; set; }
    public string?   Grower           { get; set; }
    public string?   PalletNo         { get; set; }
    public string?   GrowerPallet     { get; set; }
    public string?   PackCodeSample   { get; set; }
    public string?   DateCode         { get; set; }
    public string?   LabelValue       { get; set; }
    public string?   LotNo            { get; set; }
    public DateTime  SampleCreatedAt  { get; set; }
    public string?   SampleCreatedBy  { get; set; }

    // -------------------- Defect row (one per catalog entry per sample) --------------------
    public int       DefectId         { get; set; }
    public string?   DefectCode       { get; set; }
    public string?   DefectName       { get; set; }
    public string?   DefectCategory   { get; set; }
    public string?   SeverityCode     { get; set; }
    public decimal?  DefectValue      { get; set; }   // 0 when the operator did not enter one
    public decimal?  DefectPercentage { get; set; }
    public string?   DefectComment    { get; set; }

    // -------------------- Wide bags (flattened to columns at write-time) --------------------
    /// <summary>Material-scoped header values keyed by field_code (e.g. "PACK_CODE").</summary>
    public Dictionary<string, string?> MaterialHeaderValues { get; set; } = new();
    /// <summary>Sample-scoped header values keyed by field_code.</summary>
    public Dictionary<string, string?> SampleHeaderValues   { get; set; } = new();
    /// <summary>Sample readings keyed by reading_type_code (e.g. "BRIX").</summary>
    public Dictionary<string, string?> Readings             { get; set; } = new();
}
