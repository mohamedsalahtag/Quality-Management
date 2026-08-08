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
    public string? Status     { get; set; }
    public string? Search     { get; set; }

    // ---- Collapsible panel ----
    public string? Container  { get; set; }
    public string? Bol        { get; set; }
    public string? Po         { get; set; }
    public string? ArrivalNo  { get; set; }
    public string? Material   { get; set; }
    public string? Supplier   { get; set; }
    public string? Plant      { get; set; }
    public string? StorageLoc { get; set; }
    public string? CreatedBy  { get; set; }
    /// <summary>Arrival created date, inclusive, as the user's LOCAL date. The
    /// service converts to UTC before it reaches SQL — a.created_at is stored UTC
    /// but displayed local, so a naive comparison silently drops rows either side
    /// of the +03:00 day boundary.</summary>
    public DateTime? From     { get; set; }
    /// <summary>Arrival created date, inclusive (turned into an exclusive
    /// next-midnight bound by the service).</summary>
    public DateTime? To       { get; set; }

    /// <summary>True when any panel filter is set — the view uses this to open
    /// the panel on load so a bookmarked or shared URL doesn't look like an
    /// unexplained short list.</summary>
    public bool AnyPanelFilter =>
        !string.IsNullOrWhiteSpace(Container)
        || !string.IsNullOrWhiteSpace(Bol)
        || !string.IsNullOrWhiteSpace(Po)
        || !string.IsNullOrWhiteSpace(ArrivalNo)
        || !string.IsNullOrWhiteSpace(Material)
        || !string.IsNullOrWhiteSpace(Supplier)
        || !string.IsNullOrWhiteSpace(Plant)
        || !string.IsNullOrWhiteSpace(StorageLoc)
        || !string.IsNullOrWhiteSpace(CreatedBy)
        || From.HasValue || To.HasValue;

    /// <summary>How many panel filters are active — shown as a badge on the toggle.</summary>
    public int PanelFilterCount =>
        (string.IsNullOrWhiteSpace(Container)  ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Bol)        ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Po)         ? 0 : 1)
        + (string.IsNullOrWhiteSpace(ArrivalNo)  ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Material)   ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Supplier)   ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Plant)      ? 0 : 1)
        + (string.IsNullOrWhiteSpace(StorageLoc) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(CreatedBy)  ? 0 : 1)
        + (From.HasValue ? 1 : 0)
        + (To.HasValue   ? 1 : 0);

    public bool Any => AnyPanelFilter
                       || !string.IsNullOrWhiteSpace(Search)
                       || !string.IsNullOrWhiteSpace(Status);
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
