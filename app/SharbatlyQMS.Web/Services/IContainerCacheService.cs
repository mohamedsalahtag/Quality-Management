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
    /// Returns ONE page of pending (Container, BOL, PO) triplets ready to be
    /// promoted to an Arrival, plus the total matching count for the pager.
    /// Each row aggregates all SAP lines that share the triplet for display on
    /// /Arrivals/Pending. Paging is server-side (OFFSET/FETCH): with thousands
    /// of pending triplets, shipping them all as one HTML payload was the whole
    /// page's slowness, so only <paramref name="pageSize"/> triplets — and only
    /// the material lines belonging to them — are fetched and rendered.
    /// Optional filters narrow the result on the SQL side. Plant and PoType are
    /// exact-match (sourced from the page's dropdowns, which themselves come
    /// from <see cref="GetPendingFilterOptionsAsync"/>).
    /// <paramref name="archived"/> switches the whole page between the live
    /// pending list (false — archived containers excluded) and the archive view
    /// (true — only archived ones). Every other filter applies to both.
    /// </summary>
    Task<PendingPage> ListPendingAsync(
        string? container = null, string? bol = null, string? po = null,
        // Multi-valued: the three dropdown filters on the Pending screen accept
        // more than one value each. Null or empty means no filter.
        IReadOnlyList<string>? plant = null,
        IReadOnlyList<string>? poType = null,
        IReadOnlyList<string>? storageLoc = null,
        string? supplier = null, string? material = null,
        string? matMajor = null, string? matSubMajor = null,
        DateOnly? from = null, DateOnly? to = null,
        DateOnly? arrFrom = null, DateOnly? arrTo = null,
        int page = 1, int pageSize = 100,
        Models.PlantScope? scope = null,
        bool archived = false,
        CancellationToken ct = default);

    /// <summary>
    /// Distinct plant + po_type values present in CURRENTLY PENDING cache
    /// rows. Feeds the Plant and PO Type dropdowns on /Arrivals/Pending.
    /// Pending-only (not whole-cache) by design: a plant in the dropdown
    /// that has zero pending rows would just produce an empty result page
    /// when the operator picks it.
    /// </summary>
    Task<PendingFilterOptions> GetPendingFilterOptionsAsync(
        Models.PlantScope? scope = null, bool archived = false, CancellationToken ct = default);

    /// <summary>
    /// Count of ARCHIVED triplets the user is allowed to see. Drives the
    /// "Archived" toggle's badge on /Arrivals/Pending, so an operator can tell
    /// at a glance whether anything is filed away without opening the view.
    /// </summary>
    Task<int> CountArchivedTripletsAsync(Models.PlantScope? scope = null, CancellationToken ct = default);

    /// <summary>
    /// Archives every pending triplet whose arrival date (the grid's "Arr" date,
    /// <c>MAX(arrival_date)</c> over the triplet) falls in the given inclusive
    /// range, so the operator archives exactly the rows the list showed them.
    /// Either bound may be null for an open-ended range. Only pending rows are
    /// touched — a triplet that already has an Arrival is not in this list.
    /// The user's plant scope is applied, so a plant-scoped manager can never
    /// archive another plant's containers. Returns the number of TRIPLETS
    /// archived. Manager/Admin-gated at the controller.
    /// </summary>
    Task<int> ArchiveByArrivalDateRangeAsync(DateOnly? from, DateOnly? to, string user,
        Models.PlantScope? scope = null, CancellationToken ct = default);

    /// <summary>
    /// The exact inverse of <see cref="ArchiveByArrivalDateRangeAsync"/>: clears the
    /// archive flag on every ARCHIVED triplet whose arrival date falls in the range,
    /// putting the containers back in the pending list. Undoes a bulk archive
    /// that reached too far without paging through the archive row by row.
    /// Returns the number of triplets restored.
    /// </summary>
    Task<int> RestoreByArrivalDateRangeAsync(DateOnly? from, DateOnly? to, string user,
        Models.PlantScope? scope = null, CancellationToken ct = default);

    /// <summary>
    /// Archives (<paramref name="archived"/> true) or restores (false) a single
    /// (Container, BOL, PO) triplet — every cache row in it, so the grouped list
    /// never sees a half-archived container. Returns rows affected; 0 when the
    /// triplet no longer exists or already has an Arrival.
    /// </summary>
    Task<int> SetArchivedAsync(string containerNo, string bolNo, string ebeln,
        bool archived, string user, CancellationToken ct = default);

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
    /// Reassigns every pending cache row in the (Container, BOL, PO) triplet
    /// to <paramref name="targetPlant"/> so the container moves into that
    /// plant's Pending list and the Arrival/QO it becomes are created there.
    /// The SAP <c>plant</c> column is untouched (it is part of the UPSERT key);
    /// the override lives in its own column and survives every re-sync. Passing
    /// the container's original SAP plant clears the override. Returns the
    /// number of cache rows updated (0 when the triplet is gone / already has
    /// an arrival). Manager/Admin-gated at the controller.
    /// </summary>
    Task<int> SetPlantOverrideAsync(string containerNo, string bolNo, string ebeln,
        string targetPlant, string user, CancellationToken ct = default);

    /// <summary>
    /// The effective plant of a pending triplet — <c>COALESCE(override_plant,
    /// plant)</c>. Null when no pending row exists for the triplet. Used by the
    /// plant-scope gate before letting a manager reassign or an operator create.
    /// </summary>
    Task<string?> GetEffectivePlantAsync(string containerNo, string bolNo, string ebeln, CancellationToken ct = default);

    /// <summary>
    /// The manager-set plant override for a pending triplet, or null when none
    /// is set. Passed into <see cref="IArrivalService.CreateFromSapAsync"/> so
    /// the new arrival's header plant reflects the override.
    /// </summary>
    Task<string?> GetPlantOverrideAsync(string containerNo, string bolNo, string ebeln, CancellationToken ct = default);

    /// <summary>
    /// One-shot UPDATE that catches cache rows whose arrival already exists
    /// in qms_arrival (created via /Arrivals/Search before the cache row
    /// arrived). Runs at the end of every poll cycle.
    /// </summary>
    Task<int> ReconcileWithArrivalsAsync(CancellationToken ct = default);

    /// <summary>
    /// Copies SAP's current receive / vessel-arrival / transit values from the
    /// cache onto every arrival's shipment snapshot, so the arrival page, the
    /// QC report and the dashboard show the same dates as the pending list and
    /// the Time Bar page. Runs at the end of every sweep; returns the number of
    /// snapshots changed. See ShipmentDates for why the receive date moves.
    /// </summary>
    Task<int> RefreshArrivalSnapshotsAsync(CancellationToken ct = default);

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
    /// <summary>
    /// Archives every pending container that arrived before <paramref name="before"/>,
    /// and repairs any container whose cache lines disagree about being
    /// archived. Idempotent; a null date runs the repair only. Returns how many
    /// containers the floor archived.
    /// </summary>
    Task<int> ArchiveArrivalsBeforeAsync(DateOnly? before, CancellationToken ct = default);

    /// <param name="archiveArrivalsBefore">
    /// When set, containers that arrived before this date are archived at the
    /// end of the sweep. The sweep re-reads SAP in full every run, so a PO
    /// confirmed late arrives as a new cache row weeks after its period was
    /// archived by hand -- without this the pending list refills with old
    /// containers and the manual archive has to be repeated forever.
    /// </param>
    Task<int> RefreshFromSapAsync(DateOnly sinceReceiveDate, string triggeredBy, string triggerSource,
                                  DateOnly? archiveArrivalsBefore = null, CancellationToken ct = default);

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
/// One server-side page of pending triplets: the rows to render plus the
/// total count of matching triplets (so the view can draw the pager) and the
/// page window that produced them.
/// </summary>
public sealed record PendingPage(
    IReadOnlyList<PendingPickupRow> Rows, int Total, int Page, int PageSize);

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
    /// <summary>Effective plant — the manager override when set, otherwise SAP's
    /// plant. This is what drives visibility, the plant badge, and the Arrival
    /// that gets created.</summary>
    public string? Plant           { get; set; }
    /// <summary>The container's original SAP plant. Only differs from
    /// <see cref="Plant"/> when a manager has reassigned it; the view shows it
    /// as a "was …" sub-badge so the move is visible.</summary>
    public string? OriginalPlant   { get; set; }
    /// <summary>True when a manager has overridden this container's plant.</summary>
    public bool    IsPlantOverridden { get; set; }
    public string? StorageLocation { get; set; }
    public int     LineCount       { get; set; }
    // SQL DATE columns come back from Dapper as System.DateTime (it has no
    // built-in mapper to DateOnly). The view only reads .ToString("yyyy-MM-dd")
    // so DateTime works identically here.
    public DateTime? DocDate    { get; set; }
    /// <summary>Legacy twin of <see cref="ReceiveDate"/> (both hold SAP's
    /// Receive_Date); the column the list sorts, filters and archives on.</summary>
    public DateTime? ArrivalDate{ get; set; }
    /// <summary>SAP Receive_Date -- the branch goods receipt.</summary>
    public DateTime? ReceiveDate{ get; set; }
    /// <summary>SAP Arrival_Date -- the vessel reaching the port.</summary>
    public DateTime? PortArrivalDate { get; set; }
    /// <summary>Days in transit, straight from SAP's Transit_Days (an ETA-based
    /// figure: Arrival_Date - Sailing_Date). No arrival exists yet, so there is
    /// no discharge date to compute the real one from. Promoted from
    /// payload_json to its own cache column in M12 so the list can sort on it.</summary>
    public short?    TransitDays{ get; set; }
    public DateTime  FirstSeenAt{ get; set; }
    /// <summary>When this triplet was archived out of the pending list (UTC),
    /// or null while it is still pending. Only ever set on rows the archive
    /// view returns — the pending list filters archived triplets out.</summary>
    public DateTime? ArchivedAt { get; set; }
    /// <summary>Who archived it. Shown beside the Restore button.</summary>
    public string?   ArchivedBy { get; set; }
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
