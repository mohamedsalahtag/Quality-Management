using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public interface IClaimService
{
    /// <summary>
    /// Lists every Closed Quality Order joined with its (optional) claim row.
    /// `filter` accepts: null/"" = all, "Pending" = no claim row yet,
    /// or any of the four claim status codes. `currentUser` drives the
    /// per-row UnreadCount calculation (notes posted by someone else since
    /// the user last opened that claim's Details).
    /// </summary>
    Task<IReadOnlyList<ClaimListRow>> ListClosedQosAsync(string? filter, string? search, string currentUser);

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
