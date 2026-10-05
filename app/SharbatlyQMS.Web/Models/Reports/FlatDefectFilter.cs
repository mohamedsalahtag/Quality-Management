namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V31 (2026-06-20). Filter bag for the data-hub report. Every property
/// maps to one WHERE clause in <c>StreamFlatDefectRowsAsync</c>.
///
/// Defaults: a request that sets none of the identity filters
/// (QualityOrderId, ArrivalId) and no receive date range gets the last 30
/// days of receive date (SAP's branch goods receipt) applied by
/// <c>ReportsController.FlatDefects</c>. Was the PO date until 2026-10.
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
    public DateTime? ReceiveFrom     { get; set; }   // inclusive
    public DateTime? ReceiveTo       { get; set; }   // inclusive (whole day rolls to +1 inside)

    // The range used to be on the PO date under these names. They still bind,
    // so bookmarked links and saved Report Builder layouts keep their window --
    // now applied to the receive date.
    public DateTime? PoFrom { get => ReceiveFrom; set => ReceiveFrom ??= value; }
    public DateTime? PoTo   { get => ReceiveTo;   set => ReceiveTo   ??= value; }
}
