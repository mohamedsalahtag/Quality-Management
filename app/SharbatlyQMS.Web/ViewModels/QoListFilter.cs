namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Every filter the Quality Orders list accepts, bound straight from the query
/// string. Kept as one object so the controller action, the service and the
/// view all agree on the parameter names — and so adding a filter later means
/// one property rather than a thirteenth method argument.
///
/// <see cref="Search"/> and <see cref="Status"/> come from the always-visible
/// quick bar; everything else lives in the collapsible panel. They all AND
/// together.
/// </summary>
public class QoListFilter
{
    // ---- Quick bar (always visible) ----
    public string? Status     { get; set; }
    public string? Search     { get; set; }

    // ---- Collapsible panel ----
    public string? Container  { get; set; }
    public string? Bol        { get; set; }
    public string? Po         { get; set; }
    public string? ArrivalNo  { get; set; }
    public string? Plant      { get; set; }
    public string? StorageLoc { get; set; }
    public string? Material   { get; set; }
    public string? OpenedBy   { get; set; }
    /// <summary>QO created date, inclusive, as the user's LOCAL date. The
    /// controller converts to UTC before it reaches SQL — qo.created_at is
    /// stored UTC but displayed local, so a naive comparison silently drops
    /// rows either side of the +03:00 day boundary.</summary>
    public DateTime? From     { get; set; }
    /// <summary>QO created date, inclusive (the controller turns it into an
    /// exclusive next-midnight bound).</summary>
    public DateTime? To       { get; set; }

    /// <summary>True when any panel filter is set — the view uses this to open
    /// the panel on load so a bookmarked or shared URL doesn't look like an
    /// unexplained short list.</summary>
    public bool AnyPanelFilter =>
        !string.IsNullOrWhiteSpace(Container)
        || !string.IsNullOrWhiteSpace(Bol)
        || !string.IsNullOrWhiteSpace(Po)
        || !string.IsNullOrWhiteSpace(ArrivalNo)
        || !string.IsNullOrWhiteSpace(Plant)
        || !string.IsNullOrWhiteSpace(StorageLoc)
        || !string.IsNullOrWhiteSpace(Material)
        || !string.IsNullOrWhiteSpace(OpenedBy)
        || From.HasValue || To.HasValue;

    /// <summary>How many panel filters are active — shown as a badge on the toggle.</summary>
    public int PanelFilterCount =>
        (string.IsNullOrWhiteSpace(Container)  ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Bol)        ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Po)         ? 0 : 1)
        + (string.IsNullOrWhiteSpace(ArrivalNo)  ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Plant)      ? 0 : 1)
        + (string.IsNullOrWhiteSpace(StorageLoc) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Material)   ? 0 : 1)
        + (string.IsNullOrWhiteSpace(OpenedBy)   ? 0 : 1)
        + (From.HasValue ? 1 : 0)
        + (To.HasValue   ? 1 : 0);

    public bool Any => AnyPanelFilter
                       || !string.IsNullOrWhiteSpace(Search)
                       || !string.IsNullOrWhiteSpace(Status);
}

/// <summary>Dropdown sources for the Quality Orders filter panel. Drawn from
/// quality orders that actually exist, so no option can return an empty list.</summary>
public class QoFilterOptions
{
    public IReadOnlyList<string>          Plants           { get; init; } = Array.Empty<string>();
    /// <summary>(plant, storage-loc) pairs — storage codes repeat across plants,
    /// so the dropdown narrows to the picked plant client-side.</summary>
    public IReadOnlyList<QoPlantStorage>  StorageLocations { get; init; } = Array.Empty<QoPlantStorage>();
    public IReadOnlyList<string>          OpenedBy         { get; init; } = Array.Empty<string>();
}

public sealed record QoPlantStorage(string Plant, string Code);
