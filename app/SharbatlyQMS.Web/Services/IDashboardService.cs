using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public interface IDashboardService
{
    /// <summary>
    /// Loads the aggregated counts, status breakdowns, trend points and
    /// activity lists used to render the home dashboard. Designed to run
    /// in a small fixed number of round trips regardless of data volume.
    /// </summary>
    Task<DashboardVm> GetSummaryAsync(CancellationToken ct = default);
}
