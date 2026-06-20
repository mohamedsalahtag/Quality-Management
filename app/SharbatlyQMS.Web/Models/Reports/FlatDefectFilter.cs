namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V31 (2026-06-20). Filter bag for the data-hub report. Every property
/// maps to one WHERE clause in <c>StreamFlatDefectRowsAsync</c>.
///
/// Defaults: a request that sets none of the identity filters
/// (QualityOrderId, ArrivalId) and no PO date range gets the last 30
/// days on cc.doc_date applied by <c>ReportsController.FlatDefects</c>.
/// </summary>
public class FlatDefectFilter
{
    public long?     QualityOrderId  { get; set; }
    public long?     ArrivalId       { get; set; }
    public string?   Status          { get; set; }   // Initial / Open / Submitted / Closed / Cancelled
    public string?   Plant           { get; set; }
    public string?   MaterialGroup   { get; set; }
    public string?   MajorCategory   { get; set; }
    public string?   Variety         { get; set; }
    public string?   Origin          { get; set; }
    public string?   MaterialClass   { get; set; }   // user-facing label: "Class"
    public string?   VendorName      { get; set; }   // substring match
    public string?   VendorNo        { get; set; }   // exact SAP vendor code
    public string?   StorageLocation { get; set; }
    public string?   ContainerNo     { get; set; }
    public string?   BolNo           { get; set; }
    public string?   Ebeln           { get; set; }   // PO / Shipment number
    public string?   SampleScope     { get; set; }
    public DateTime? PoFrom          { get; set; }   // inclusive
    public DateTime? PoTo            { get; set; }   // inclusive (whole day rolls to +1 inside)
}
