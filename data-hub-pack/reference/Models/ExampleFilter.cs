namespace YourApp.Models.Reports;

/// <summary>
/// data-hub-pack reference.
///
/// THIS IS A TEMPLATE. The host project replaces this class with its own
/// filter type. Whatever properties live here become the slots
/// <c>IDataHubFilterAdapter</c> can push down to SQL.
///
/// The Sharbatly QMS instantiation of this is <c>FlatDefectFilter</c> with
/// 18 slots (PO date range, status, plant, material group, vendor, etc.).
/// See <c>examples/fruit-quality-defects-data-source.cs</c> for that mapping.
/// </summary>
public class ExampleFilter
{
    public DateTime? FromDate    { get; set; }   // inclusive
    public DateTime? ToDate      { get; set; }   // inclusive (rolled to +1 day server-side)
    public string?   Status      { get; set; }
    public string?   Plant       { get; set; }
    public string?   VendorName  { get; set; }   // substring match
    public long?     PrimaryId   { get; set; }   // identity filter -- disables date defaults

    // Add or remove slots to match your domain. Every slot maps to one
    // optional WHERE clause in IDataHubFilterAdapter.AppendFilterWhere.
}
