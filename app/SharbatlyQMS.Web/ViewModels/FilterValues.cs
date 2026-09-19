namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Turning a multi-select filter's submitted values into something safe to bind
/// into a Dapper <c>IN @list</c> clause.
///
/// Two separate jobs, deliberately not merged:
///
///   <see cref="Many"/> cleans — trims, drops blanks, de-duplicates. An unticked
///   box submits nothing, but a hand-edited query string can still carry
///   "?plant=", and an empty string would match no plant and silently empty the
///   page.
///
///   <see cref="ForIn"/> guarantees the list is never empty, because Dapper
///   renders an empty <c>IN</c> as invalid SQL on this version — the same reason
///   <c>PlantScope.QueryPlants</c> has carried a sentinel since it was written.
///   Getting this wrong does not fail quietly: every page whose filter binds one
///   of these returns 500.
///
/// So the count that decides whether a filter is ACTIVE must come from the
/// cleaned list, while the value BOUND into SQL comes from ForIn. Pair it with
/// an any-flag, exactly as the plant scope does:
///
///     var plants = FilterValues.Many(f.Plant);
///     ... new { plants = FilterValues.ForIn(plants), plantAny = plants.Count > 0 }
///     ... "AND (@plantAny = 0 OR a.plant IN @plants)"
///
/// The flag is what turns the filter off; the sentinel only keeps the SQL legal.
/// </summary>
public static class FilterValues
{
    /// <summary>A value no real code equals, so an IN against it matches
    /// nothing. Leading space is deliberate — no SAP code carries one.</summary>
    private static readonly IReadOnlyList<string> NoMatch = new[] { " __none__" };

    public static List<string> Many(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The list to bind into <c>IN @param</c> — never empty.</summary>
    public static IReadOnlyList<string> ForIn(IReadOnlyList<string> cleaned) =>
        cleaned.Count > 0 ? cleaned : NoMatch;
}
