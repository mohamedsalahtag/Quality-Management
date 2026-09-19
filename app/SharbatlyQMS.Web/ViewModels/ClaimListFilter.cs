namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Every filter the Claims list accepts, bound straight from the query string.
/// Deliberately mirrors <see cref="QoListFilter"/> — the Claims page lists the
/// same underlying Closed Quality Orders, so the two panels look and behave the
/// same and a user only learns one set of controls.
///
/// <see cref="Status"/> and <see cref="Search"/> come from the always-visible
/// quick bar; everything else lives in the collapsible panel. They all AND
/// together.
///
/// The one difference from the QO panel: the dates filter on the QO's
/// <c>closed_at</c>, not <c>created_at</c>. When a claim is what you're looking
/// at, the date that matters is when the inspection was finished.
/// </summary>
public class ClaimListFilter
{
    /// <summary>Which tab is showing. False = the active Claims worklist
    /// (unarchived, Closed only); true = the Archived tab, which carries every
    /// archived order whatever its QO status. Set by the controller action, not
    /// bound from the query string, so it cannot be spoofed into a mixed list.</summary>
    public bool Archived { get; set; }

    // ---- Quick bar (always visible) ----
    /// <summary>"" = all, "Pending" = no claim row yet, otherwise a claim status code.</summary>
    /// <summary>Claim statuses to include. Multi-valued because the common
    /// question is "what is still open?", which is Pending + Claim Notification
    /// Request + Hold — three statuses a single-value chip could never express
    /// at once. Empty means no status filter.</summary>
    public List<string> Status { get; set; } = new();
    /// <summary>M24 QC assessment picked at finish: "" = any, "Yes" = Potential
    /// Claim, "No" = No Potential Claim, "Unset" = finished before the question
    /// existed. ANDs with <see cref="Status"/> — the two answer different
    /// questions (what QC saw vs. what the claim team decided).</summary>
    public string? Potential  { get; set; }
    public string? Search     { get; set; }

    // ---- Collapsible panel ----
    public string? Container  { get; set; }
    public string? Bol        { get; set; }
    public string? Po         { get; set; }
    public string? ArrivalNo  { get; set; }
    public List<string> Plant      { get; set; } = new();
    public List<string> StorageLoc { get; set; } = new();
    public string? Material   { get; set; }
    public List<string> Supplier { get; set; } = new();
    /// <summary>Who finished the Quality Order (qo.closed_by).</summary>
    public List<string> ClosedBy { get; set; } = new();
    /// <summary>Who last acted on the claim (qms_claim.last_changed_by).</summary>
    public List<string> ClaimOwner { get; set; } = new();
    /// <summary>QO closed date, inclusive, as the user's LOCAL date. The service
    /// converts to UTC before it reaches SQL — qo.closed_at is stored UTC but
    /// displayed local, so a naive comparison silently drops rows either side of
    /// the +03:00 day boundary.</summary>
    public DateTime? From     { get; set; }
    /// <summary>QO closed date, inclusive (turned into an exclusive
    /// next-midnight bound by the service).</summary>
    public DateTime? To       { get; set; }

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
        + (Plant.Count      == 0 ? 0 : 1)
        + (StorageLoc.Count == 0 ? 0 : 1)
        + (string.IsNullOrWhiteSpace(Material)   ? 0 : 1)
        + (Supplier.Count   == 0 ? 0 : 1)
        + (ClosedBy.Count   == 0 ? 0 : 1)
        + (ClaimOwner.Count == 0 ? 0 : 1)
        + (From.HasValue ? 1 : 0)
        + (To.HasValue   ? 1 : 0);

    public bool Any => AnyPanelFilter
                       || !string.IsNullOrWhiteSpace(Search)
                       || Status.Count > 0
                       || !string.IsNullOrWhiteSpace(Potential);
}

/// <summary>Dropdown sources for the Claims filter panel. Drawn from Closed
/// Quality Orders that actually exist (and are inside the caller's plant
/// scope), so no option can return an empty list.</summary>
public class ClaimFilterOptions
{
    public IReadOnlyList<string>         Plants           { get; init; } = Array.Empty<string>();
    /// <summary>(plant, storage-loc) pairs — storage codes repeat across plants,
    /// so the dropdown narrows to the picked plant client-side.</summary>
    public IReadOnlyList<QoPlantStorage> StorageLocations { get; init; } = Array.Empty<QoPlantStorage>();
    public IReadOnlyList<string>         Suppliers        { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string>         ClosedBy         { get; init; } = Array.Empty<string>();
    /// <summary>Distinct qms_claim.last_changed_by — only users who have
    /// actually touched a claim appear.</summary>
    public IReadOnlyList<string>         ClaimOwners      { get; init; } = Array.Empty<string>();
}
