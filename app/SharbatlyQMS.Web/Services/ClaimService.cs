using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;

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

    public async Task<IReadOnlyList<ClaimListRow>> ListClosedQosAsync(
        ClaimListFilter f, PlantScope scope, string currentUser)
    {
        // Pending = a Closed QO with NO row in qms_claim yet.
        // Other statuses = qms_claim.claim_status equality.
        // "All" / null = no claim-status WHERE clause.
        var pendingFilter = f.Status == ClaimStatus.Pending;
        var statusFilter  = !string.IsNullOrEmpty(f.Status) && !pendingFilter ? f.Status : null;

        static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        // qo.closed_at is UTC; the list renders it local. Convert the picked
        // LOCAL dates to UTC here, and make the upper bound exclusive-next-
        // midnight rather than BETWEEN, which would drop everything after
        // 00:00:00.000 on the To date. Same handling as QualityOrderService.
        DateTime? fromUtc = f.From.HasValue
            ? DateTime.SpecifyKind(f.From.Value.Date, DateTimeKind.Local).ToUniversalTime()
            : null;
        DateTime? toUtc = f.To.HasValue
            ? DateTime.SpecifyKind(f.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            : null;

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
                   qo.status_code        AS StatusCode,
                   qo.archived_at        AS ArchivedAt,
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
            -- Two mutually exclusive buckets. The active worklist is Closed
            -- orders only (a claim decision presupposes a finished inspection);
            -- the archive holds every archived order whatever its status, which
            -- is why the status test is inside the @archived branch.
            WHERE  (
                     (@archived = 0 AND qo.archived_at IS NULL AND qo.status_code = 'Closed')
                  OR (@archived = 1 AND qo.archived_at IS NOT NULL)
                   )
              AND  (@pendingFilter = 0 OR cl.claim_id IS NULL)
              AND  (@statusFilter IS NULL OR cl.claim_status = @statusFilter)
              -- Per-user plant scope. The Details action already enforces this;
              -- without it here a plant-scoped user saw other plants' claims in
              -- the list, and the new Plant dropdown would have widened that.
              AND  (@sUnrestricted = 1 OR a.plant IN @sPlants)
              AND  (@plant      IS NULL OR a.plant            = @plant)
              AND  (@storageLoc IS NULL OR a.storage_location = @storageLoc)
              AND  (@supplier   IS NULL OR a.vendor_name      = @supplier)
              AND  (@closedBy   IS NULL OR qo.closed_by       = @closedBy)
              AND  (@claimOwner IS NULL OR cl.last_changed_by = @claimOwner)
              AND  (@container  IS NULL OR a.container_no LIKE '%' + @container + '%')
              AND  (@bol        IS NULL OR a.bol_no       LIKE '%' + @bol       + '%')
              AND  (@po         IS NULL OR a.ebeln        LIKE '%' + @po        + '%')
              AND  (@arrivalNo  IS NULL OR a.arrival_no   LIKE '%' + @arrivalNo + '%')
              AND  (@fromUtc    IS NULL OR qo.closed_at  >= @fromUtc)
              AND  (@toUtc      IS NULL OR qo.closed_at   < @toUtc)
              AND  (@material   IS NULL OR EXISTS (
                        SELECT 1 FROM qms_quality_order_material m2
                        WHERE  m2.quality_order_id = qo.quality_order_id
                          AND (m2.material_no   LIKE '%' + @material + '%'
                            OR m2.material_desc LIKE '%' + @material + '%')))
              AND  (@search IS NULL OR
                    qo.quality_order_no LIKE '%' + @search + '%' OR
                    a.container_no      LIKE '%' + @search + '%' OR
                    a.bol_no            LIKE '%' + @search + '%' OR
                    a.ebeln             LIKE '%' + @search + '%' OR
                    a.vendor_name       LIKE '%' + @search + '%' OR
                    qo.closed_by        LIKE '%' + @search + '%')
            ORDER BY
                COALESCE(cl.last_changed_at, qo.closed_at, qo.opened_at, qo.created_at) DESC,
                qo.quality_order_id DESC";

        using var c = Open();
        var rows = await c.QueryAsync<ClaimListRow>(sql, new
        {
            pendingFilter,
            statusFilter,
            archived      = f.Archived,
            sUnrestricted = scope.Unrestricted,
            sPlants       = scope.QueryPlants,
            plant         = Trim(f.Plant),
            storageLoc    = Trim(f.StorageLoc),
            supplier      = Trim(f.Supplier),
            closedBy      = Trim(f.ClosedBy),
            claimOwner    = Trim(f.ClaimOwner),
            container     = Trim(f.Container),
            bol           = Trim(f.Bol),
            po            = Trim(f.Po),
            arrivalNo     = Trim(f.ArrivalNo),
            material      = Trim(f.Material),
            search        = Trim(f.Search),
            fromUtc,
            toUtc,
            currentUser
        });
        return rows.ToList();
    }

    /// <summary>Dropdown sources for the Claims filter panel. Restricted to
    /// Closed QOs inside the caller's plant scope, so an option can never come
    /// back with zero matching rows.</summary>
    public async Task<ClaimFilterOptions> GetClaimFilterOptionsAsync(PlantScope scope, bool archived = false)
    {
        using var c = Open();
        // @bucket repeats the list's own WHERE so a dropdown never offers a
        // value that returns nothing on the tab you are looking at.
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT a.plant
            FROM   qms_quality_order qo
            JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
            WHERE  ((@archived = 0 AND qo.archived_at IS NULL AND qo.status_code = 'Closed')
                   OR (@archived = 1 AND qo.archived_at IS NOT NULL))
              AND  a.plant IS NOT NULL AND a.plant <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.plant;

            SELECT DISTINCT a.plant AS Plant, a.storage_location AS Code
            FROM   qms_quality_order qo
            JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
            WHERE  ((@archived = 0 AND qo.archived_at IS NULL AND qo.status_code = 'Closed')
                   OR (@archived = 1 AND qo.archived_at IS NOT NULL))
              AND  a.plant            IS NOT NULL AND a.plant            <> ''
              AND  a.storage_location IS NOT NULL AND a.storage_location <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.plant, a.storage_location;

            SELECT DISTINCT a.vendor_name
            FROM   qms_quality_order qo
            JOIN   qms_arrival a ON a.arrival_id = qo.arrival_id
            WHERE  ((@archived = 0 AND qo.archived_at IS NULL AND qo.status_code = 'Closed')
                   OR (@archived = 1 AND qo.archived_at IS NOT NULL))
              AND  a.vendor_name IS NOT NULL AND a.vendor_name <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.vendor_name;

            SELECT DISTINCT qo.closed_by
            FROM   qms_quality_order qo
            LEFT   JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
            WHERE  ((@archived = 0 AND qo.archived_at IS NULL AND qo.status_code = 'Closed')
                   OR (@archived = 1 AND qo.archived_at IS NOT NULL))
              AND  qo.closed_by IS NOT NULL AND qo.closed_by <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY qo.closed_by;

            SELECT DISTINCT cl.last_changed_by
            FROM   qms_claim cl
            JOIN   qms_quality_order qo ON qo.quality_order_id = cl.quality_order_id
            LEFT   JOIN qms_arrival a ON a.arrival_id = qo.arrival_id
            WHERE  ((@archived = 0 AND qo.archived_at IS NULL AND qo.status_code = 'Closed')
                   OR (@archived = 1 AND qo.archived_at IS NOT NULL))
              AND  cl.last_changed_by IS NOT NULL AND cl.last_changed_by <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY cl.last_changed_by;",
            new { sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants, archived });

        return new ClaimFilterOptions
        {
            Plants           = (await grid.ReadAsync<string>()).ToList(),
            StorageLocations = (await grid.ReadAsync<QoPlantStorage>()).ToList(),
            Suppliers        = (await grid.ReadAsync<string>()).ToList(),
            ClosedBy         = (await grid.ReadAsync<string>()).ToList(),
            ClaimOwners      = (await grid.ReadAsync<string>()).ToList()
        };
    }

    public async Task MarkSeenAsync(long qualityOrderId, string user)
    {
        // Upsert: if a row exists for (user, claim_for_qo) bump it to NOW,
        // otherwise insert. No-op when the QO doesn't have a claim row yet
        // (nothing to mark seen).
        using var c = Open();
        await c.ExecuteAsync(@"
            -- HOLDLOCK closes the MERGE upsert race: opening the same claim in
            -- two tabs at once could otherwise both take the NOT MATCHED branch
            -- and throw a PK violation.
            MERGE qms_claim_read_marker WITH (HOLDLOCK) AS tgt
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
            // Guard on decided_at IS NULL: if the Claim Manager committed a
            // decision between our LoadClaimAsync above and this write, the
            // UPDATE affects 0 rows and we abort — otherwise the QM status write
            // would silently overwrite the CM's decision (leaving e.g. a
            // "Passed QC" status alongside the CM's decision timestamp).
            var qmAffected = await c.ExecuteAsync(@"
                UPDATE qms_claim
                SET    claim_status    = @toStatus,
                       last_changed_at = SYSUTCDATETIME(),
                       last_changed_by = @user
                WHERE  claim_id = @claimId AND decided_at IS NULL",
                new { claimId, toStatus, user }, tx);
            if (qmAffected != 1)
            {
                tx.Rollback();
                return (false, "The Claim Manager has already decided on this claim. Quality Manager can no longer change the status.");
            }
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

        // Guard on the status we loaded so a concurrent CM flip (or a QM revert)
        // between LoadClaimAsync and this write is detected instead of silently
        // last-writer-wins.
        var cmAffected = await c.ExecuteAsync(@"
            UPDATE qms_claim
            SET    claim_status    = @toStatus,
                   last_changed_at = SYSUTCDATETIME(),
                   last_changed_by = @user,
                   decided_at      = COALESCE(decided_at, SYSUTCDATETIME()),
                   decided_by      = COALESCE(decided_by, @user)
            WHERE  claim_id = @claimId AND claim_status = @expected",
            new { claimId = existing.ClaimId, toStatus, user, expected = existing.ClaimStatus }, tx);
        if (cmAffected != 1)
        {
            tx.Rollback();
            return (false, "This claim was changed by someone else. Please refresh and try again.");
        }

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
