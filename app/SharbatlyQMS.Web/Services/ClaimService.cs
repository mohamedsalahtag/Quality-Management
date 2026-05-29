using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public class ClaimService : IClaimService
{
    private readonly string _cs;
    private readonly IAuditService _audit;

    public ClaimService(IConfiguration config, IAuditService audit)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
    }

    private SqlConnection Open() => new(_cs);

    // ---- Read paths -------------------------------------------------

    public async Task<IReadOnlyList<ClaimListRow>> ListClosedQosAsync(string? filter, string? search, string currentUser)
    {
        // Pending = a Closed QO with NO row in qms_claim yet.
        // Other filters = qms_claim.claim_status equality.
        // "All" / null = no claim-status WHERE clause.
        var pendingFilter = filter == ClaimStatus.Pending;
        var statusFilter  = !string.IsNullOrEmpty(filter) && !pendingFilter ? filter : null;

        // Unread = notes added on this claim after the user's last_seen mark
        // AND not authored by the user themselves. If no read-marker row
        // exists yet, every other-author note counts as unread.
        const string sql = @"
            SELECT qo.quality_order_id   AS QualityOrderId,
                   qo.quality_order_no   AS QualityOrderNo,
                   qo.arrival_id         AS ArrivalId,
                   a.arrival_no          AS ArrivalNo,
                   a.container_no        AS ContainerNo,
                   a.bol_no              AS BolNo,
                   a.ebeln               AS Ebeln,
                   a.vendor_name         AS VendorName,
                   qo.closed_at          AS ClosedAt,
                   qo.closed_by          AS ClosedBy,
                   cl.claim_status       AS ClaimStatus,
                   cl.last_changed_at    AS LastActivityAt,
                   cl.decided_at         AS DecidedAt,
                   ISNULL(nc.note_count,   0) AS NoteCount,
                   ISNULL(uc.unread_count, 0) AS UnreadCount
            FROM   qms_quality_order qo
            LEFT   JOIN qms_arrival  a  ON a.arrival_id = qo.arrival_id
            LEFT   JOIN qms_claim    cl ON cl.quality_order_id = qo.quality_order_id
            LEFT   JOIN qms_claim_read_marker rm
                   ON rm.claim_id = cl.claim_id AND rm.user_name = @currentUser
            OUTER  APPLY (
                SELECT COUNT(*) AS note_count
                FROM   qms_claim_note cn
                WHERE  cn.claim_id = cl.claim_id
            ) nc
            OUTER  APPLY (
                SELECT COUNT(*) AS unread_count
                FROM   qms_claim_note cn
                WHERE  cn.claim_id   = cl.claim_id
                  AND  cn.created_by <> @currentUser
                  AND  (rm.last_seen_at IS NULL OR cn.created_at > rm.last_seen_at)
            ) uc
            WHERE  qo.status_code = 'Closed'
              AND  (@pendingFilter = 0 OR cl.claim_id IS NULL)
              AND  (@statusFilter IS NULL OR cl.claim_status = @statusFilter)
              AND  (@search IS NULL OR
                    qo.quality_order_no LIKE '%' + @search + '%' OR
                    a.container_no      LIKE '%' + @search + '%' OR
                    a.bol_no            LIKE '%' + @search + '%' OR
                    a.ebeln             LIKE '%' + @search + '%' OR
                    a.vendor_name       LIKE '%' + @search + '%' OR
                    qo.closed_by        LIKE '%' + @search + '%')
            ORDER BY
                COALESCE(cl.last_changed_at, qo.closed_at) DESC,
                qo.quality_order_id DESC";

        using var c = Open();
        var rows = await c.QueryAsync<ClaimListRow>(sql, new
        {
            pendingFilter,
            statusFilter,
            search = string.IsNullOrWhiteSpace(search) ? null : search,
            currentUser
        });
        return rows.ToList();
    }

    public async Task MarkSeenAsync(long qualityOrderId, string user)
    {
        // Upsert: if a row exists for (user, claim_for_qo) bump it to NOW,
        // otherwise insert. No-op when the QO doesn't have a claim row yet
        // (nothing to mark seen).
        using var c = Open();
        await c.ExecuteAsync(@"
            MERGE qms_claim_read_marker AS tgt
            USING (
                SELECT cl.claim_id, @user AS user_name
                FROM   qms_claim cl
                WHERE  cl.quality_order_id = @qualityOrderId
            ) AS src
                ON  tgt.claim_id = src.claim_id
                AND tgt.user_name = src.user_name
            WHEN MATCHED THEN
                UPDATE SET last_seen_at = SYSUTCDATETIME()
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (user_name, claim_id, last_seen_at)
                VALUES (src.user_name, src.claim_id, SYSUTCDATETIME());",
            new { qualityOrderId, user });
    }

    public async Task<(QualityClaim? claim, IReadOnlyList<ClaimNote> notes)> GetForQoAsync(long qualityOrderId)
    {
        using var c = Open();
        var claim = await c.QuerySingleOrDefaultAsync<QualityClaim>(@"
            SELECT claim_id         AS ClaimId,
                   quality_order_id AS QualityOrderId,
                   claim_status     AS ClaimStatus,
                   created_at       AS CreatedAt,
                   created_by       AS CreatedBy,
                   last_changed_at  AS LastChangedAt,
                   last_changed_by  AS LastChangedBy,
                   decided_at       AS DecidedAt,
                   decided_by       AS DecidedBy
            FROM   qms_claim
            WHERE  quality_order_id = @qualityOrderId",
            new { qualityOrderId });

        if (claim == null) return (null, Array.Empty<ClaimNote>());

        var notes = await c.QueryAsync<ClaimNote>(@"
            SELECT note_id        AS NoteId,
                   claim_id       AS ClaimId,
                   note_text      AS NoteText,
                   note_kind      AS NoteKind,
                   status_at_post AS StatusAtPost,
                   created_at     AS CreatedAt,
                   created_by     AS CreatedBy,
                   author_role    AS AuthorRole
            FROM   qms_claim_note
            WHERE  claim_id = @claimId
            ORDER  BY created_at ASC, note_id ASC",
            new { claimId = claim.ClaimId });

        return (claim, notes.ToList());
    }

    // ---- Quality Manager actions -----------------------------------

    public Task<(bool ok, string? error)> MarkClaimRequestAsync(long qoId, string note, string user, string authorRole)
        => QmTransitionAsync(qoId, note, user, authorRole, ClaimStatus.ClaimRequest);

    public Task<(bool ok, string? error)> MarkPassedQcAsync(long qoId, string note, string user, string authorRole)
        => QmTransitionAsync(qoId, note, user, authorRole, ClaimStatus.PassedQC);

    private async Task<(bool ok, string? error)> QmTransitionAsync(
        long qoId, string note, string user, string authorRole, string toStatus)
    {
        if (string.IsNullOrWhiteSpace(note))
            return (false, "A note is required when changing claim status.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        if (!await IsClosedAsync(c, tx, qoId))
            return (false, "Quality Order must be Closed.");

        var existing = await LoadClaimAsync(c, tx, qoId);

        // QM lock-out: once CM has decided, QM cannot change status (can still post a comment via AddNoteAsync).
        if (existing != null && existing.DecidedAt != null)
            return (false, "The Claim Manager has already decided on this claim. Quality Manager can no longer change the status.");

        long claimId;
        if (existing == null)
        {
            claimId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_claim
                    (quality_order_id, claim_status, created_at, created_by, last_changed_at, last_changed_by)
                VALUES (@qoId, @toStatus, SYSUTCDATETIME(), @user, SYSUTCDATETIME(), @user);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { qoId, toStatus, user }, tx);
        }
        else
        {
            claimId = existing.ClaimId;
            await c.ExecuteAsync(@"
                UPDATE qms_claim
                SET    claim_status    = @toStatus,
                       last_changed_at = SYSUTCDATETIME(),
                       last_changed_by = @user
                WHERE  claim_id = @claimId",
                new { claimId, toStatus, user }, tx);
        }

        await InsertNoteAsync(c, tx, claimId, note, ClaimNoteKind.StatusChange, toStatus, user, authorRole);

        // T021 (US1) -- domain action labels (ClaimRequest / PassedQC) preserved
        // per FR-002 and the 2026-05-20 clarification.
        var qmAction = toStatus == ClaimStatus.ClaimRequest
            ? ActionCodes.ClaimRequest
            : ActionCodes.PassedQC;
        await _audit.WriteAsync(c, tx,
            EntityTypes.Claim, claimId, qmAction,
            oldValues: existing == null ? null : new { claim_status = existing.ClaimStatus },
            newValues: new { claim_status = toStatus, note },
            actor: user);

        tx.Commit();
        return (true, null);
    }

    // ---- Claim Manager actions -------------------------------------

    public Task<(bool ok, string? error)> ApproveAsync(long qoId, string note, string user, string authorRole)
        => CmTransitionAsync(qoId, note, user, authorRole, ClaimStatus.ClaimRequestApproved);

    public Task<(bool ok, string? error)> HoldAsync(long qoId, string note, string user, string authorRole)
        => CmTransitionAsync(qoId, note, user, authorRole, ClaimStatus.HoldClaim);

    private async Task<(bool ok, string? error)> CmTransitionAsync(
        long qoId, string note, string user, string authorRole, string toStatus)
    {
        if (string.IsNullOrWhiteSpace(note))
            return (false, "A note is required when deciding a claim.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        if (!await IsClosedAsync(c, tx, qoId))
            return (false, "Quality Order must be Closed.");

        var existing = await LoadClaimAsync(c, tx, qoId);
        if (existing == null)
            return (false, "Cannot decide a claim that the Quality Manager has not yet raised.");

        // CM can act when status is ClaimRequest, or flip between Approved/Hold once decided.
        var allowed = existing.ClaimStatus == ClaimStatus.ClaimRequest
                   || existing.ClaimStatus == ClaimStatus.ClaimRequestApproved
                   || existing.ClaimStatus == ClaimStatus.HoldClaim;
        if (!allowed)
            return (false, $"Claim is in status '{ClaimStatus.Label(existing.ClaimStatus)}' -- Claim Manager has nothing to decide.");

        await c.ExecuteAsync(@"
            UPDATE qms_claim
            SET    claim_status    = @toStatus,
                   last_changed_at = SYSUTCDATETIME(),
                   last_changed_by = @user,
                   decided_at      = COALESCE(decided_at, SYSUTCDATETIME()),
                   decided_by      = COALESCE(decided_by, @user)
            WHERE  claim_id = @claimId",
            new { claimId = existing.ClaimId, toStatus, user }, tx);

        await InsertNoteAsync(c, tx, existing.ClaimId, note, ClaimNoteKind.StatusChange, toStatus, user, authorRole);

        // T021 (US1) -- domain action labels (Approved / Hold) preserved.
        var cmAction = toStatus == ClaimStatus.ClaimRequestApproved
            ? ActionCodes.Approved
            : ActionCodes.Hold;
        await _audit.WriteAsync(c, tx,
            EntityTypes.Claim, existing.ClaimId, cmAction,
            oldValues: new { claim_status = existing.ClaimStatus, decided_at = existing.DecidedAt, decided_by = existing.DecidedBy },
            newValues: new { claim_status = toStatus, decided_by = user, note },
            actor: user);

        tx.Commit();
        return (true, null);
    }

    // ---- Free-text chat note ---------------------------------------

    public async Task<(bool ok, string? error)> AddNoteAsync(long qoId, string note, string user, string authorRole)
    {
        if (string.IsNullOrWhiteSpace(note))
            return (false, "Note text is required.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        if (!await IsClosedAsync(c, tx, qoId))
            return (false, "Quality Order must be Closed.");

        var existing = await LoadClaimAsync(c, tx, qoId);
        if (existing == null)
            return (false, "Cannot add a note before the Quality Manager has marked this QO.");

        await InsertNoteAsync(c, tx, existing.ClaimId, note, ClaimNoteKind.Comment, null, user, authorRole);

        // T021 (US1) -- plain claim note recorded as a ClaimNote.Created entry.
        await _audit.WriteAsync(c, tx,
            EntityTypes.ClaimNote, existing.ClaimId, ActionCodes.Created,
            oldValues: null,
            newValues: new { note },
            actor: user);

        tx.Commit();
        return (true, null);
    }

    // ---- Helpers ---------------------------------------------------

    private static async Task<bool> IsClosedAsync(SqlConnection c, SqlTransaction tx, long qoId)
    {
        var status = await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT status_code FROM qms_quality_order WHERE quality_order_id = @qoId",
            new { qoId }, tx);
        return status == "Closed";
    }

    private static async Task<QualityClaim?> LoadClaimAsync(SqlConnection c, SqlTransaction tx, long qoId)
    {
        return await c.QuerySingleOrDefaultAsync<QualityClaim>(@"
            SELECT claim_id         AS ClaimId,
                   quality_order_id AS QualityOrderId,
                   claim_status     AS ClaimStatus,
                   created_at       AS CreatedAt,
                   created_by       AS CreatedBy,
                   last_changed_at  AS LastChangedAt,
                   last_changed_by  AS LastChangedBy,
                   decided_at       AS DecidedAt,
                   decided_by       AS DecidedBy
            FROM   qms_claim
            WHERE  quality_order_id = @qoId",
            new { qoId }, tx);
    }

    private static Task InsertNoteAsync(
        SqlConnection c, SqlTransaction tx,
        long claimId, string noteText, string noteKind, string? statusAtPost,
        string user, string authorRole)
    {
        return c.ExecuteAsync(@"
            INSERT INTO qms_claim_note
                (claim_id, note_text, note_kind, status_at_post, created_at, created_by, author_role)
            VALUES (@claimId, @noteText, @noteKind, @statusAtPost, SYSUTCDATETIME(), @user, @authorRole)",
            new { claimId, noteText, noteKind, statusAtPost, user, authorRole }, tx);
    }
}
