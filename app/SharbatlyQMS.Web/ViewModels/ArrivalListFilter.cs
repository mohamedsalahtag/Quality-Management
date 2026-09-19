namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Every filter the Arrivals list accepts, bound straight from the query string.
/// Mirrors <see cref="QoListFilter"/> so the two list pages behave identically:
/// <see cref="Search"/> and <see cref="Status"/> are the always-visible quick
/// bar; everything else lives in the collapsible "More filters" panel. They all
/// AND together.
/// </summary>
public class ArrivalListFilter
{
    // ---- Quick bar (always visible) ----
    /// <summary>Arrival statuses to include. Multi-valued: "what is not
    /// finished" spans Draft and In Progress, which a single-value chip could
    /// not express. Empty means no status filter.</summary>
    public List<string> Status { get; set; } = new();
    public string? Search     { get; set; }

    // ---- Collapsible panel ----
    public string? Container  { get; set; }
    public string? Bol        { get; set; }
    public string? Po         { get; set; }
    public string? ArrivalNo  { get; set; }
    public string? Material   { get; set; }
    /// <summary>Material master major category, e.g. Apples or Bananas.</summary>
    public string? MatMajor    { get; set; }
    /// <summary>Sub-major under that major, e.g. a variety group.</summary>
    public string? MatSubMajor { get; set; }

    public List<string> Supplier   { get; set; } = new();
    public List<string> Plant      { get; set; } = new();
    public List<string> StorageLoc { get; set; } = new();
    public List<string> CreatedBy  { get; set; } = new();
    /// <summary>Arrival created date, inclusive, as the user's LOCAL date. The
    /// service converts to UTC before it reaches SQL — a.created_at is stored UTC
    /// but displayed local, so a naive comparison silently drops rows either side
    /// of the +03:00 day boundary.</summary>
    public DateTime? From     { get; set; }
    /// <summary>Arrival created date, inclusive (turned into an exclusive
    /// next-midnight bound by the service).</summary>
    public DateTime? To       { get; set; }

    // ---- Server-side page window ----
    // The list used to ship every arrival and let the browser paginate. At 729
    // rows that was a 2 MB payload the browser laid out in full -- and because a
    // table sizes its columns from every rendered row, the page painted ~180px
    // too wide for the ~2.5s before the script cut it back. Only one page of
    // rows crosses the wire now. Mirrors /Arrivals/Pending.
    public int Page     { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    /// <summary>True when any panel filter is set — the view uses this to open
    /// the panel on load so a bookmarked or shared URL doesn't look like an
    /// unexplained short list.</summary>
    public bool AnyPanelFilter => PanelFilterCount > 0;

    /// <summary>How many panel filters are active — shown as a badge on the
    /// toggle. A multi-select counts as ONE filter however many values it
    /// holds: "Plant" is one thing the user narrowed by, and counting its
    /// values would make the badge read like a row count.</summary>
    public int PanelFilterCount =>
        (string.IsNullOrWhiteSpace(Container)  ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Bol)        ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Po)         ? 0 : 1)
        + (string.IsNullOrWhiteSpace(ArrivalNo)  ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Material)   ? 0 : 1)
        + (string.IsNullOrWhiteSpace(MatMajor)    ? 0 : 1)
        + (string.IsNullOrWhiteSpace(MatSubMajor) ? 0 : 1)
        + (Supplier.Count   == 0 ? 0 : 1)
        + (Plant.Count      == 0 ? 0 : 1)
        + (StorageLoc.Count == 0 ? 0 : 1)
        + (CreatedBy.Count  == 0 ? 0 : 1)
        + (From.HasValue ? 1 : 0)
        + (To.HasValue   ? 1 : 0);

    public bool Any => AnyPanelFilter
                       || !string.IsNullOrWhiteSpace(Search)
                       || Status.Count > 0;
}

/// <summary>Dropdown sources for the Arrivals filter panel. Drawn from arrivals
/// that actually exist, so no option can return an empty list. Reuses
/// <see cref="QoPlantStorage"/> for the (plant, storage-loc) pairs.</summary>
public class ArrivalFilterOptions
{
    public IReadOnlyList<string>         Plants           { get; init; } = Array.Empty<string>();
    public IReadOnlyList<QoPlantStorage> StorageLocations { get; init; } = Array.Empty<QoPlantStorage>();
    public IReadOnlyList<string>         Suppliers        { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>         CreatedBy        { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One server-side page of arrivals: the rows to render, the total number that
/// match the filter (so the pager knows how many pages there are), and the
/// window that produced them. Mirrors <c>PendingPage</c> on the Pending
/// Containers page, which solved the same problem first.
/// </summary>
public sealed record ArrivalPage(
    IReadOnlyList<Models.Arrival> Rows, int Total, int Page, int PageSize);
