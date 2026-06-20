using SharbatlyQMS.Web.Models.Reports;

namespace SharbatlyQMS.Web.Services.Reports;

/// <summary>
/// V34 (2026-06-20). CRUD for the Perspective Analyzer's saved configs.
/// Role gating: scope='shared' is rejected when canShare=false; delete /
/// set-default are owner-only (SiteAdmin bypasses ownership).
/// </summary>
public interface IPerspectiveService
{
    Task<IReadOnlyList<PerspectiveDto>> ListForUserAsync(string reportKey,
        string username, CancellationToken ct);

    Task<PerspectiveDto?> GetAsync(long id, string username, CancellationToken ct);

    /// <param name="canShare">True for Manager / SiteAdmin. Determines
    /// whether <c>scope='shared'</c> is honoured or forced back to private.</param>
    Task<PerspectiveDto> SaveAsync(PerspectiveSaveRequest req, string username,
        bool canShare, CancellationToken ct);

    Task<bool> DeleteAsync(long id, string username, bool isSiteAdmin, CancellationToken ct);

    Task<bool> SetDefaultAsync(long id, string username, CancellationToken ct);
}
