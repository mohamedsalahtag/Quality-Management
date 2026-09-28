namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// One multi-select filter control: a dropdown of checkboxes that submits the
/// same field name once per ticked value, so `?plant=JD01&amp;plant=RD02` binds
/// straight to a <c>List&lt;string&gt;</c> on the filter model.
///
/// Checkboxes rather than a native <c>&lt;select multiple&gt;</c>: the native
/// control needs ctrl-click to add a second value and silently drops the whole
/// selection on a mis-click, which is exactly the accident a filter must not
/// have. It also cannot show a count, and the panel's job is to say at a glance
/// how narrow the current view is.
///
/// Submission needs no JavaScript at all — same-named checkboxes are a browser
/// primitive. multiselect.js only keeps the button's summary text honest while
/// the menu is open.
/// </summary>
public sealed class MultiSelectFilterVm
{
    /// <summary>Query-string field name, e.g. "plant". Must match the filter
    /// model's property name for binding to find it.</summary>
    public string Name { get; init; } = "";

    public string Label { get; init; } = "";

    /// <summary>Shown on the button when nothing is ticked, e.g. "All plants".
    /// Says what the unfiltered view contains rather than just "All".</summary>
    public string AllLabel { get; init; } = "All";

    public IReadOnlyList<MultiSelectOption> Options { get; init; } = Array.Empty<MultiSelectOption>();

    public IReadOnlyList<string> Selected { get; init; } = Array.Empty<string>();

    /// <summary>Bootstrap column classes, so each page keeps its own layout.</summary>
    public string ColumnCss { get; init; } = "col-6 col-md-3";

    /// <summary>
    /// Adds a search box at the top of the menu that narrows the list as you
    /// type (part of a name, any case). For long lists such as suppliers. The
    /// box has no name, so it is never submitted and never becomes a filter by
    /// itself -- only the ticked boxes do.
    /// </summary>
    public bool Searchable { get; init; }

    /// <summary>Placeholder for the search box, e.g. "Search supplier name…".</summary>
    public string SearchPlaceholder { get; init; } = "Search…";

    /// <summary>
    /// Values that are selected but no longer offered — a bookmarked filter, or
    /// a vendor whose last order was archived. Listed anyway and flagged, so a
    /// short list is explained instead of looking like the filter broke.
    /// </summary>
    public IEnumerable<string> Orphans =>
        Selected.Where(v => !string.IsNullOrWhiteSpace(v)
                            && !Options.Any(o => string.Equals(o.Value, v, StringComparison.OrdinalIgnoreCase)));

    public bool IsSelected(string value) =>
        Selected.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Button text: the single value, a count, or the "all" wording.</summary>
    public string Summary => Selected.Count switch
    {
        0 => AllLabel,
        1 => Options.FirstOrDefault(o => string.Equals(o.Value, Selected[0], StringComparison.OrdinalIgnoreCase))
                    ?.Display ?? Selected[0],
        _ => $"{Selected.Count} selected"
    };
}

/// <param name="Value">What goes in the query string and the SQL.</param>
/// <param name="Display">What the user reads — a plant name, not its code.</param>
public sealed record MultiSelectOption(string Value, string Display);
