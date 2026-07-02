using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The single write path to qms_audit_log. WriteAsync MUST be called
/// inside the caller's open transaction (conn + tx are passed in) so
/// the audit row commits atomically with the originating mutation
/// (FR-007). This (conn, tx) shape is intentional and is the only
/// service in the codebase that takes the caller's transaction --
/// see specs/001-audit-trail/research.md §R-5.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Writes one audit-log row inside the caller's transaction.
    /// `oldValues` and `newValues` are anonymous objects (or null);
    /// they're serialised as JSON with System.Text.Json. Skip the call
    /// entirely if the diff is empty -- no-op updates are not logged
    /// per FR-016.
    /// </summary>
    Task WriteAsync(SqlConnection conn, SqlTransaction tx,
        string entityType, long entityId, string actionCode,
        object? oldValues, object? newValues, string actor);

    /// <summary>
    /// Self-contained audit write for administrative / master-data mutations
    /// (user-role changes, catalog and configuration edits, the Danger-Zone
    /// purge) that are single-statement operations not already wrapped in a
    /// caller transaction. Opens its own connection. Use the transactional
    /// overload above for operational mutations that MUST be atomic with their
    /// business write (FR-007).
    /// </summary>
    Task WriteAsync(string entityType, long entityId, string actionCode,
        object? oldValues, object? newValues, string actor);

    /// <summary>
    /// Per-record history fetch (FR-008). Point lookup via the existing
    /// IX_qms_audit_log_entity index. Returns newest first.
    /// </summary>
    Task<IReadOnlyList<AuditEntryListRow>> GetForRecordAsync(
        string entityType, long entityId);

    /// <summary>
    /// Per-record history including the record's children, so the panel
    /// on the QO / Arrival / Claim Details pages shows every mutation
    /// the user can perform from that page (not just transitions on the
    /// top-level record). For QualityOrder this includes its materials,
    /// samples, and the samples' readings/defects/header values; for
    /// Arrival its checklist + items; for Claim its notes. For any other
    /// entity type this falls back to <see cref="GetForRecordAsync"/>.
    /// </summary>
    Task<IReadOnlyList<AuditEntryListRow>> GetForCompositeRecordAsync(
        string entityType, long entityId);

    /// <summary>
    /// Global filtered list (FR-009, FR-010). Keyset-paginated via the
    /// new IX_qms_audit_log_filter index. Body added in Phase 4 (US2).
    /// </summary>
    Task<IReadOnlyList<AuditEntryListRow>> ListAsync(AuditFilter filter);

    /// <summary>
    /// Streams every matching entry within the date range for the .xlsx
    /// export (FR-012). Yields rows in chunks so the full result set is
    /// never buffered in memory.
    /// </summary>
    IAsyncEnumerable<AuditEntry> ExportAsync(AuditFilter filter, CancellationToken ct = default);
}
