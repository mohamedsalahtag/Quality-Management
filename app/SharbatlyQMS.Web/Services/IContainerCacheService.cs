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
    /// Plant and PoType are exact-match (sourced from the page's
    /// dropdowns, which themselves come from
    /// <see cref="GetPendingFilterOptionsAsync"/>).
    /// </summary>
    Task<IReadOnlyList<PendingPickupRow>> ListPendingAsync(
        string? container = null, string? bol = null, string? po = null,
        string? plant = null, string? poType = null, string? storageLoc = null,
        string? supplier = null,
        CancellationToken ct = default);

    /// <summary>
    /// Distinct plant + po_type values present in CURRENTLY PENDING cache
    /// rows. Feeds the Plant and PO Type dropdowns on /Arrivals/Pending.
    /// Pending-only (not whole-cache) by design: a plant in the dropdown
    /// that has zero pending rows would just produce an empty result page
    /// when the operator picks it.
    /// </summary>
    Task<PendingFilterOptions> GetPendingFilterOptionsAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Snapshot of the latest completed pull + whether one is currently
    /// in flight. Single source of truth for the Settings card AND the
    /// Pending Containers page banner.
    /// </summary>
    Task<ContainerPullStatus> GetPullStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Read model returned by <see cref="IContainerCacheService.GetPullStatusAsync"/>.
/// Backed by the latest row(s) in <c>qms_sap_sync_log</c> for
/// <c>endpoint_key = 'ContainerCache'</c>.
/// </summary>
public class ContainerPullStatus
{
    /// <summary>When the last completed pull finished (UTC). Null if none yet.</summary>
    public DateTime? LastRunUtc    { get; set; }
    /// <summary>Human-readable result of the last completed pull -- "Fetched N…" or "FAILED: …".</summary>
    public string?   LastResult    { get; set; }
    public int?      LastRowCount  { get; set; }
    /// <summary>"Manual" or "Auto" for the last completed pull.</summary>
    public string?   LastTriggerSource { get; set; }
    /// <summary>The user / scheduler that fired the last completed pull.</summary>
    public string?   LastTriggeredBy   { get; set; }
    /// <summary>True iff a sync_log row exists with completed_at IS NULL.</summary>
    public bool      IsRunning     { get; set; }
    /// <summary>When the in-flight pull started (UTC). Only set when <see cref="IsRunning"/> is true.</summary>
    public DateTime? RunningSince  { get; set; }
    /// <summary>"Manual" or "Auto" for the in-flight pull.</summary>
    public string?   RunningTriggerSource { get; set; }
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
    /// <summary>Stock Transport Order. Sourced from ZQC_Data.PO_Number in the new endpoint shape.</summary>
    public string? Sto             { get; set; }
    public string? VendorNo        { get; set; }
    public string? VendorName      { get; set; }
    public string? PoType          { get; set; }
    public string? Plant           { get; set; }
    public string? StorageLocation { get; set; }
    public int     LineCount       { get; set; }
    // SQL DATE columns come back from Dapper as System.DateTime (it has no
    // built-in mapper to DateOnly). The view only reads .ToString("yyyy-MM-dd")
    // so DateTime works identically here.
    public DateTime? DocDate    { get; set; }
    public DateTime? ArrivalDate{ get; set; }
    public DateTime? ReceiveDate{ get; set; }
    /// <summary>Days in transit, straight from SAP's Transit_Days. Promoted from
    /// payload_json to its own cache column in M12 so the list can sort on it.</summary>
    public short?    TransitDays{ get; set; }
    public DateTime  FirstSeenAt{ get; set; }
    /// <summary>Per-PO-line materials inside this triplet, ordered by Ebelp.</summary>
    public List<PendingMaterialLine> MaterialLines { get; set; } = new();
}

/// <summary>
/// Dropdown sources for the Pending Containers filter form. All lists
/// are sorted alphabetically and exclude null / empty values. The
/// <see cref="StorageLocations"/> list carries the parent plant code so
/// the page can render a Plant-dependent dropdown (storage-loc codes
/// repeat across plants -- "0001" exists under multiple plants).
/// </summary>
public class PendingFilterOptions
{
    public IReadOnlyList<string>           Plants           { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>           PoTypes          { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PlantStorageLoc>  StorageLocations { get; init; } = Array.Empty<PlantStorageLoc>();
}

/// <summary>(Plant, storage-loc) pair drawn from currently-pending cache rows.</summary>
public sealed record PlantStorageLoc(string Plant, string Code);

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
