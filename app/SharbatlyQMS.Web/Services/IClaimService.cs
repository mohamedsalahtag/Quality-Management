using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public interface IClaimService
{
    /// <summary>
    /// Lists every Closed Quality Order joined with its (optional) claim row.
    /// `filter.Status` accepts: null/"" = all, "Pending" = no claim row yet,
    /// or any claim status code; the rest of the filter narrows on the arrival
    /// and QO the claim hangs off. `scope` limits the list to the caller's
    /// plants. `currentUser` drives the per-row UnreadCount calculation (notes
    /// posted by someone else since the user last opened that claim's Details).
    /// </summary>
    Task<IReadOnlyList<ClaimListRow>> ListClosedQosAsync(
        ClaimListFilter filter, PlantScope scope, string currentUser);

    /// <summary>Dropdown sources for the Claims filter panel, plant-scoped and
    /// drawn from the tab being shown so no option comes back empty.</summary>
    Task<ClaimFilterOptions> GetClaimFilterOptionsAsync(PlantScope scope, bool archived = false);

    Task<(QualityClaim? claim, IReadOnlyList<ClaimNote> notes)> GetForQoAsync(long qualityOrderId);

    /// <summary>Upserts the read-marker row for (user, claim_for_qo) to NOW.</summary>
    Task MarkSeenAsync(long qualityOrderId, string user);

    // ---- Quality Manager actions ----
    // Service enforces "decided_at IS NULL" guard. Caller (controller) is
    // responsible for [Authorize(Policy = ManagerOrAdmin)].
    Task<(bool ok, string? error)> MarkClaimRequestAsync(long qoId, string note, string user, string authorRole);
    Task<(bool ok, string? error)> MarkPassedQcAsync   (long qoId, string note, string user, string authorRole);

    // ---- Claim Manager actions ----
    // First CM action stamps decided_at/by. CM may flip Approved <-> Hold freely.
    // Controller gates with [Authorize(Policy = ClaimManagerOrAdmin)].
    Task<(bool ok, string? error)> ApproveAsync(long qoId, string note, string user, string authorRole);
    Task<(bool ok, string? error)> HoldAsync   (long qoId, string note, string user, string authorRole);

    // ---- Free-text chat note (any allowed role) ----
    Task<(bool ok, string? error)> AddNoteAsync(long qoId, string note, string user, string authorRole);
}
