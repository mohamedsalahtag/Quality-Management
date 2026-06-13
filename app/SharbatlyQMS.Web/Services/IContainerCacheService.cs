using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Read/write access to qms_sap_container_cache. Owns the UPSERT contract
/// from the polling service, the page-query for /Arrivals/Pending, and the
/// arrival-linkage mutations triggered by ArrivalsController.Create.
/// </summary>
public interface IContainerCacheService
{
    /// <summary>
    /// MERGE each SAP row into the cache. Refreshes denormalised columns
    /// + last_seen_at; never overwrites has_arrival / arrival_id (those
    /// are owned by MarkArrivedAsync and ReconcileWithArrivalsAsync).
    /// Returns the count of inserted + updated rows.
    /// </summary>
    Task<int> UpsertAsync(IReadOnlyList<SapShipmentRow> rows, CancellationToken ct = default);

    /// <summary>
    /// Lists every pending (Container, BOL, PO) triplet ready to be
    /// promoted to an Arrival. Each row aggregates all SAP lines that
    /// share the triplet for display on /Arrivals/Pending. No server-side
    /// limit -- the page's client-side tablekit paginator handles long
    /// lists. Optional filters narrow the result on the SQL side so the
    /// search box on the page does not have to ship a huge HTML payload.
    /// </summary>
    Task<IReadOnlyList<PendingPickupRow>> ListPendingAsync(
        string? container = null, string? bol = null, string? po = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns every cached SapShipmentRow for the given (Container, BOL, PO)
    /// triplet. Used by ArrivalsController.Create when invoked from
    /// /Arrivals/Pending so the arrival can be built from the cache without
    /// a fresh SAP round-trip.
    /// </summary>
    Task<IReadOnlyList<SapShipmentRow>> GetTripletRowsAsync(string containerNo, string bolNo, string ebeln, CancellationToken ct = default);

    /// <summary>
    /// Flags every cache row in the (Container, BOL, PO) triplet as having
    /// an arrival, stamping the new arrival_id. Called from
    /// ArrivalsController.Create on success.
    /// </summary>
    Task MarkArrivedAsync(string containerNo, string bolNo, string ebeln, long arrivalId, CancellationToken ct = default);

    /// <summary>
    /// One-shot UPDATE that catches cache rows whose arrival already exists
    /// in qms_arrival (created via /Arrivals/Search before the cache row
    /// arrived). Runs at the end of every poll cycle.
    /// </summary>
    Task<int> ReconcileWithArrivalsAsync(CancellationToken ct = default);

    /// <summary>
    /// Dashboard tile feed: count of triplets pending an Arrival.
    /// </summary>
    Task<int> CountPendingTripletsAsync(CancellationToken ct = default);

    /// <summary>
    /// One end-to-end pull from SAP: fetch every row with TOC_DATE on/after
    /// <paramref name="sinceDocDate"/>, UPSERT into the cache, run reconcile,
    /// and write a row to qms_sap_sync_log. Shared by the background puller
    /// and the admin "Pull now" button so both go through the same code path.
    /// Returns the number of SAP rows fetched.
    /// </summary>
    Task<int> RefreshFromSapAsync(DateOnly sinceDocDate, string triggeredBy, string triggerSource, CancellationToken ct = default);
}

/// <summary>
/// One row on /Arrivals/Pending: a (Container, BOL, PO) triplet ready to
/// be promoted to an Arrival.
/// </summary>
public class PendingPickupRow
{
    public string  ContainerNo  { get; set; } = "";
    public string  BolNo        { get; set; } = "";
    public string  Ebeln        { get; set; } = "";
    public string? VendorNo     { get; set; }
    public string? VendorName   { get; set; }
    public string? PoType       { get; set; }
    public int     LineCount    { get; set; }
    // SQL DATE columns come back from Dapper as System.DateTime (it has no
    // built-in mapper to DateOnly). The view only reads .ToString("yyyy-MM-dd")
    // so DateTime works identically here.
    public DateTime? DocDate    { get; set; }
    public DateTime? ArrivalDate{ get; set; }
    public DateTime? ReceiveDate{ get; set; }
    public DateTime  FirstSeenAt{ get; set; }
    /// <summary>Per-PO-line materials inside this triplet, ordered by Ebelp.</summary>
    public List<PendingMaterialLine> MaterialLines { get; set; } = new();
}

public class PendingMaterialLine
{
    public string  ContainerNo  { get; set; } = "";
    public string  BolNo        { get; set; } = "";
    public string  Ebeln        { get; set; } = "";
    public string  Ebelp        { get; set; } = "";
    public string  MaterialNo   { get; set; } = "";
    public string? MaterialDesc { get; set; }
    public string? MaterialGroup{ get; set; }
    public decimal? Quantity    { get; set; }
    public string?  Uom         { get; set; }
}
