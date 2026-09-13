using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public interface IDashboardService
{
    /// <summary>
    /// Loads the aggregated counts, status breakdowns, trend points and
    /// activity lists used to render the home dashboard, narrowed to one
    /// plant and one period. Designed to run in a small fixed number of round
    /// trips regardless of data volume.
    ///
    /// <paramref name="scope"/> is the user's plant entitlement and is applied
    /// on top of <paramref name="filter"/>: a plant-restricted operator can
    /// pick between their own plants and no others, and "all plants" means all
    /// the plants they hold. The filter can narrow the scope, never widen it.
    /// </summary>
    Task<DashboardVm> GetSummaryAsync(DashboardFilter filter, PlantScope scope,
        CancellationToken ct = default);

    /// <summary>
    /// The containers behind one number on the Received vs Inspected portlet.
    ///
    /// Driven by the same period, plant scope and definitions as the portlet
    /// itself, so a drill-through can never disagree with the figure that was
    /// clicked. <paramref name="plant"/> narrows to a single row of the table;
    /// null returns every plant in scope.
    /// </summary>
    Task<IReadOnlyList<CommitmentDetailRow>> GetCommitmentDetailAsync(
        DashboardFilter filter, PlantScope scope, string bucket, string? plant,
        CancellationToken ct = default);
}
