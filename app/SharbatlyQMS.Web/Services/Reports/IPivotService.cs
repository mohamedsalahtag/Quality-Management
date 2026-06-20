using SharbatlyQMS.Web.Models.Reports;

namespace SharbatlyQMS.Web.Services.Reports;

/// <summary>
/// V34 (2026-06-20). Server-side pivot for the Perspective Analyzer.
/// </summary>
public interface IPivotService
{
    /// <summary>
    /// Runs a dynamic GROUP BY against the report's whitelisted view.
    /// Throws <see cref="ArgumentException"/> for any dimension / measure /
    /// aggregation that is not registered in <see cref="PivotRegistry"/>.
    /// </summary>
    Task<PivotResult> RunAsync(PivotRequest request, CancellationToken ct);

    /// <summary>
    /// V34.1 (2026-06-20). Returns the distinct values of a single dimension,
    /// scoped by the active filter (or whole dataset when ignorePageFilter is
    /// true). Used by the drill-filter values picker. Capped at 200 rows; sets
    /// <paramref name="truncated"/> when the cap was hit. Cached short-term in
    /// IMemoryCache to handle repeated dropdown opens.
    /// </summary>
    Task<(IReadOnlyList<string> Values, bool Truncated)> GetDistinctValuesAsync(
        string reportKey, string dimKey, FlatDefectFilter filter,
        bool ignorePageFilter, string? search, CancellationToken ct);
}
