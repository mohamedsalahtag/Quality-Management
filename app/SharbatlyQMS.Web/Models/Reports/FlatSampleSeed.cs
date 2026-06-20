namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V31 (2026-06-20). Dapper target for the first query of the flat-report
/// pipeline. One row per qms_sample matching the filter, joined with its
/// QO + material + arrival + a SINGLE picked container-cache row (via
/// OUTER APPLY TOP 1 -- avoids the cardinality blow-up of joining only
/// container_no+bol_no+ebeln against a cache whose unique key is wider).
/// Header values, readings, and defects are batched in subsequent queries
/// keyed by SampleId / QoMaterialId.
/// </summary>
public class FlatSampleSeed
{
    public long      SampleId         { get; set; }
    public long      QoMaterialId     { get; set; }
    public long      QualityOrderId   { get; set; }
    // Sample
    public int       SampleNo         { get; set; }
    public string?   SampleScope      { get; set; }
    public short?    SampleSize       { get; set; }
    public bool      SizeOverridden   { get; set; }
    public string?   Grower           { get; set; }
    public string?   PalletNo         { get; set; }
    public string?   GrowerPallet     { get; set; }
    public string?   PackCode         { get; set; }
    public string?   DateCode         { get; set; }
    public string?   LabelValue       { get; set; }
    public string?   LotNo            { get; set; }
    public DateTime  CreatedAt        { get; set; }
    public string?   CreatedBy        { get; set; }
    // QO
    public string?   QualityOrderNo   { get; set; }
    public string?   StatusCode       { get; set; }
    public DateTime? QoCreatedAt      { get; set; }
    // Material (qms_quality_order_material persisted snapshot; some columns are
    // MARA-overridden by StreamFlatDefectRowsAsync when the snapshot is NULL).
    public string?   MaterialNo       { get; set; }
    public string?   MaterialDesc     { get; set; }
    public string?   MaterialGroup    { get; set; }
    public string?   MaterialGroupDesc{ get; set; }
    public string?   MajorCategory    { get; set; }
    public string?   SubMajorCategory { get; set; }  // MARA-only (not in QOM)
    public string?   Variety          { get; set; }
    public string?   MaterialClass    { get; set; }
    public string?   Origin           { get; set; }
    public string?   Brand            { get; set; }
    public string?   PackType         { get; set; }
    // Note: PackCode is sample-scoped (s.pack_code, defined above). The
    // material-level pack code is not exposed in this report -- if needed,
    // add it as a separate property and pull from MARA.
    public string?   MaterialSize     { get; set; }
    public short?    MaterialSampleSize { get; set; }
    // Arrival item
    public decimal?  Quantity         { get; set; }
    public string?   Uom              { get; set; }
    public string?   StorageLocation  { get; set; }  // V31: ai.storage_location
    // Arrival
    public string?   ArrivalNo        { get; set; }
    public string?   Plant            { get; set; }
    public string?   ContainerNo      { get; set; }
    public string?   BolNo            { get; set; }
    public string?   Ebeln            { get; set; }
    public string?   VendorName       { get; set; }
    public string?   VendorNo         { get; set; }  // V31: a.vendor_no
    // SAP container cache (one row via OUTER APPLY TOP 1)
    public string?   Sto              { get; set; }
    public DateTime? PoDate           { get; set; }  // V31: cc.doc_date
    // Dates: prefer authoritative qms_shipment_snapshot, fall back to cc cache.
    public DateTime? ArrivalDate      { get; set; }
    public DateTime? ReceiveDate      { get; set; }
    // Shipment snapshot (LEFT JOIN qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id)
    public DateTime? LoadingDate      { get; set; }  // V31: ss.loading_date
    public DateTime? ShippingDate     { get; set; }  // V31: ss.sailing_date (vessel departure)
    public short?    TransitDays      { get; set; }  // V31: ss.transit_days
}
