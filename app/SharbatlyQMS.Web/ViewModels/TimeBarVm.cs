using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Everything the Time Bar page accepts from the query string. Bound as one
/// object so the action, the service and the view agree on the names, and so a
/// shared URL reproduces exactly what the sender was looking at.
/// </summary>
public class TimeBarFilter
{
    /// <summary>One box across container / BOL / PO / arrival no / QO no / supplier.</summary>
    public string? Search   { get; set; }

    /// <summary>Pending | Arrival | QC. See <see cref="TimeBarStages"/>.</summary>
    public string? Stage    { get; set; }
    /// <summary>The raw arrival or quality-order status code.</summary>
    public string? Status   { get; set; }
    public string? Plant    { get; set; }
    public string? PoType   { get; set; }
    /// <summary>Material master major category, e.g. Apples.</summary>
    public string? MatMajor    { get; set; }
    /// <summary>Sub-major under that major.</summary>
    public string? MatSubMajor { get; set; }
    public string? Supplier { get; set; }
    public string? Container{ get; set; }
    public string? Bol      { get; set; }
    public string? Po       { get; set; }

    /// <summary>Arrival-date range on the clock's START date.</summary>
    public DateOnly? From   { get; set; }
    public DateOnly? To     { get; set; }

    /// <summary>Only rows past the warning threshold — the reason to open the page.</summary>
    public bool OverOnly        { get; set; }
    /// <summary>Containers the SAP cache has never seen (arrivals created through
    /// /Arrivals/Search). A data-quality finding in its own right.</summary>
    public bool NoCacheOnly     { get; set; }
    /// <summary>Archived containers are out of the working list by default.</summary>
    public bool IncludeArchived { get; set; }
    /// <summary>Elapsed at least this many days.</summary>
    public int? MinDays     { get; set; }

    public string? Sort     { get; set; }
    public string? Dir      { get; set; }
    public int  Page        { get; set; } = 1;
    public int  PageSize    { get; set; } = 100;

    public bool Any =>
        !string.IsNullOrWhiteSpace(Search)   || !string.IsNullOrWhiteSpace(Stage)
        || !string.IsNullOrWhiteSpace(Status)|| !string.IsNullOrWhiteSpace(Plant)
        || !string.IsNullOrWhiteSpace(PoType)|| !string.IsNullOrWhiteSpace(Supplier)
        || !string.IsNullOrWhiteSpace(Container) || !string.IsNullOrWhiteSpace(Bol)
        || !string.IsNullOrWhiteSpace(Po)    || From.HasValue || To.HasValue
        || !string.IsNullOrWhiteSpace(MatMajor) || !string.IsNullOrWhiteSpace(MatSubMajor)
        || OverOnly || NoCacheOnly || MinDays.HasValue;
}

/// <summary>How far along the process a container has got.</summary>
public static class TimeBarStages
{
    /// <summary>SAP sent it; nobody has created an arrival. The clock is running
    /// and no one is looking at it — the rows this page exists for.</summary>
    public const string Pending = "Pending";
    /// <summary>An arrival exists but no quality order does.</summary>
    public const string Arrival = "Arrival";
    /// <summary>A quality order exists; its status is the container's status.</summary>
    public const string Qc      = "QC";

    public static readonly string[] All = { Pending, Arrival, Qc };
}

/// <summary>One container's line on the Time Bar page.</summary>
public class TimeBarRow
{
    public string  ContainerNo { get; set; } = "";
    public string  BolNo       { get; set; } = "";
    public string  Ebeln       { get; set; } = "";
    public string? Sto         { get; set; }
    public string? VendorName  { get; set; }
    public string? Plant       { get; set; }
    public string? PoType      { get; set; }

    /// <summary>The clock's start: the container's arrival date.</summary>
    public DateTime? ArrivalDate { get; set; }
    /// <summary>Where that date came from — Receipt, Snapshot or Port. Shown as a
    /// small badge so a reader can tell which date fed the bar.</summary>
    public string?   ArrivalSource { get; set; }

    public long?     ArrivalId   { get; set; }
    public string?   ArrivalNo   { get; set; }
    public long?     QualityOrderId { get; set; }
    public string?   QualityOrderNo { get; set; }
    /// <summary>The clock's end when QC has finished. Null while it is running.</summary>
    public DateTime? ClosedAt    { get; set; }

    public string  Stage      { get; set; } = TimeBarStages.Pending;
    public string? StatusCode { get; set; }

    /// <summary>Days between the arrival date and the QC finish — or between the
    /// arrival date and now while it is still running. Null when no arrival date
    /// is known, in which case the page shows a dash rather than inventing one.</summary>
    public int?  ElapsedDays { get; set; }
    /// <summary>True while there is no finished quality order: the number is
    /// still growing.</summary>
    public bool  IsRunning   { get; set; }
    /// <summary>QC finished BEFORE the recorded arrival date. Happens with
    /// back-dated receipts. The elapsed value is clamped to 0 and flagged rather
    /// than printed negative, which would read as a bug in the page.</summary>
    public bool  IsBackwards { get; set; }
    /// <summary>The container is not in the SAP cache at all.</summary>
    public bool  NotInCache  { get; set; }
    public bool  IsArchived  { get; set; }

    /// <summary>What to print in the Status column. Quality-order codes go
    /// through DisplayName so "Closed" reads as "Finished", the word the rest of
    /// the application uses.</summary>
    public string StatusLabel => Stage switch
    {
        TimeBarStages.Pending => "Pending",
        TimeBarStages.Qc      => QualityOrderStatus.DisplayName(StatusCode),
        _                     => string.IsNullOrWhiteSpace(StatusCode) ? "—" : StatusCode!
    };

    /// <summary>success / warning / danger / secondary, from the configured
    /// thresholds. Kept out of the view so the page and any future export agree.</summary>
    public string Tone(int goodDays, int warnDays) =>
        ElapsedDays is null       ? "secondary"
        : ElapsedDays <= goodDays ? "success"
        : ElapsedDays <= warnDays ? "warning"
        :                           "danger";
}

/// <summary>One page of Time Bar rows plus the totals the header prints.</summary>
public sealed record TimeBarPage(
    IReadOnlyList<TimeBarRow> Rows, int Total, int Page, int PageSize,
    TimeBarSummary Summary);

/// <summary>
/// The one-line answer a manager wants before reading any row. Computed over
/// the WHOLE filtered set, not just the visible page.
/// </summary>
public class TimeBarSummary
{
    public int Containers   { get; set; }
    /// <summary>No arrival created — nobody has started.</summary>
    public int Pending      { get; set; }
    /// <summary>Clock still running (no finished quality order).</summary>
    public int Running      { get; set; }
    /// <summary>Past the warning threshold.</summary>
    public int OverThreshold{ get; set; }
    /// <summary>Median elapsed days among the settled rows. Median, not mean:
    /// one container stuck for 90 days would drag an average somewhere no
    /// individual container actually is.</summary>
    public int? MedianSettled { get; set; }
}

/// <summary>Dropdown sources, drawn from the rows the user can actually see.</summary>
public class TimeBarFilterOptions
{
    public IReadOnlyList<string> Plants   { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PoTypes  { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Statuses { get; init; } = Array.Empty<string>();
}
