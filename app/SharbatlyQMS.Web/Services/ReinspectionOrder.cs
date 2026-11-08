using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The quality order raised when a finished inspection is judged unsound and
/// the container has to be inspected again.
///
/// Static and transaction-scoped, like <see cref="RejectionOrder"/> and
/// <see cref="QoCascade"/>, so the whole reinspection — the new order, its
/// materials, the supersede stamp on the original, the MOVED claim, history and
/// audit — commits or rolls back as one unit. A half-applied reinspection would
/// leave a container with two live orders, an original superseded by an order
/// that was never created, or a claim thread belonging to neither.
/// </summary>
public static class ReinspectionOrder
{
    /// <summary>
    /// Raises a reinspection of <paramref name="originalQoId"/>.
    ///
    /// The new order starts exactly as a first inspection does: materials
    /// copied from the arrival, NO samples. Copying the first inspection's
    /// samples would anchor the second one to the result being questioned, and
    /// a carried-over reading or photo could pass for re-checked work.
    ///
    /// The inspection criteria need no copying at all: defects, readings and
    /// tolerances are resolved live per material group whenever a form or a
    /// report is built, so the reinspection uses whatever the catalogue says
    /// today.
    ///
    /// Returns the new order's id and number.
    /// </summary>
    public static async Task<(long QoId, string QoNo)> CreateAsync(
        SqlConnection c, SqlTransaction tx, IAuditService audit,
        long originalQoId, string reason, string user)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new InvalidOperationException(
                "Say why this order is being reinspected — the reason is recorded against the claim.");

        // Every clause re-checked inside the transaction. The view hides the
        // button, but the view is not the security boundary, and two admins
        // clicking at once must not both win.
        var original = await c.QuerySingleOrDefaultAsync<ReinspectCandidate>(@"
            SELECT quality_order_id  QoId,
                   quality_order_no  QoNo,
                   arrival_id        ArrivalId,
                   status_code       StatusCode,
                   container_rejected ContainerRejected,
                   reinspection_of   ReinspectionOf,
                   superseded_at     SupersededAt,
                   archived_at       ArchivedAt
            FROM   qms_quality_order
            WHERE  quality_order_id = @originalQoId", new { originalQoId }, tx);

        if (original is null)
            throw new InvalidOperationException("That quality order no longer exists.");
        if (original.StatusCode != QualityOrderStatus.Closed)
            throw new InvalidOperationException(
                $"Only a finished order can be reinspected (this one is {QualityOrderStatus.DisplayName(original.StatusCode)}).");
        if (original.ContainerRejected)
            throw new InvalidOperationException(
                "This order exists because the container was refused on arrival. Nothing was inspected, so there is nothing to inspect again.");
        if (original.ReinspectionOf.HasValue)
            throw new InvalidOperationException(
                "This order is itself a reinspection. A container is reinspected once.");
        if (original.SupersededAt.HasValue)
            throw new InvalidOperationException(
                "This order has already been reinspected.");
        if (original.ArchivedAt.HasValue)
            throw new InvalidOperationException(
                "This order is archived. Restore it from the Claims archive first, then reinspect.");

        var arrivalId = original.ArrivalId;

        var seq = await c.ExecuteScalarAsync<int>(
            "SELECT NEXT VALUE FOR seq_qms_quality_order_no", transaction: tx);
        var qoNo = $"QO-{DateTime.UtcNow:yyyy}-{seq:D6}";

        // Stamp the original FIRST. UX_qms_quality_order_active_per_arrival is
        // unique on arrival_id where the order is neither cancelled nor
        // superseded, so the new row cannot be inserted while the original
        // still holds that slot. Doing it in this order also means a failure
        // anywhere below rolls the supersede back with everything else.
        var stamped = await c.ExecuteAsync(@"
            UPDATE qms_quality_order
            SET    superseded_at = SYSUTCDATETIME(),
                   superseded_by = @user
            WHERE  quality_order_id = @originalQoId
              AND  superseded_at IS NULL",
            new { originalQoId, user }, tx);
        if (stamped != 1)
            throw new InvalidOperationException(
                "This order was reinspected by someone else a moment ago.");

        var qoId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_quality_order
                (quality_order_no, arrival_id, status_code, reinspection_of, created_at, created_by)
            VALUES
                (@qoNo, @arrivalId, 'Initial', @originalQoId, SYSUTCDATETIME(), @user);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { qoNo, arrivalId, originalQoId, user }, tx);

        // The same seven identity columns both existing creation paths copy.
        // Everything else on a QO material (sizes, overrides, header values) is
        // filled during the inspection, which is the point of doing it again.
        await c.ExecuteAsync(@"
            INSERT INTO qms_quality_order_material
                (quality_order_id, arrival_item_id, material_no, material_desc,
                 material_group, material_group_desc, major_category)
            SELECT @qoId, ai.arrival_item_id, ai.material_no, ai.material_desc,
                   ai.material_group, ai.material_group_desc, ai.major_category
            FROM   qms_arrival_item ai
            WHERE  ai.arrival_id = @arrivalId",
            new { qoId, arrivalId }, tx);

        // The claim MOVES to the reinspection. One container, one conversation:
        // the thread opened about the first inspection is the same thread that
        // settles the second one, and the reinspection is what speaks for the
        // container everywhere else now.
        //
        // MOVED, not copied, and the row keeps its claim_id -- qms_claim_note
        // and qms_claim_read_marker are keyed on claim_id, so the whole history
        // and every reader's last-seen mark follow without being touched.
        // Copying would restart the conversation and re-flag read notes as
        // unread for everybody.
        //
        // The Claim Manager's lock-out is honoured rather than bypassed: once
        // they have decided, a Quality Manager cannot change the status, and
        // neither can this. Refusing here is the same rule, not a new one --
        // silently moving a settled verdict onto an inspection that has not
        // happened yet would be worse than refusing.
        var claim = await c.QuerySingleOrDefaultAsync<ClaimRow>(@"
            SELECT claim_id ClaimId, claim_status ClaimStatus, decided_at DecidedAt
            FROM   qms_claim WHERE quality_order_id = @originalQoId",
            new { originalQoId }, tx);

        if (claim is { DecidedAt: not null })
            throw new InvalidOperationException(
                "The Claim Manager has already decided this claim. Reopen that decision before reinspecting.");

        long claimId;
        if (claim is null)
        {
            // Nothing was ever raised against the first inspection -- claims are
            // created lazily, so a Closed order with no row reads as "Pending".
            // Start the thread on the reinspection, which is where the rest of
            // the conversation is going to happen.
            claimId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_claim
                    (quality_order_id, claim_status, created_at, created_by,
                     last_changed_at, last_changed_by)
                VALUES (@qoId, @status, SYSUTCDATETIME(), @user, SYSUTCDATETIME(), @user);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { qoId, status = ClaimStatus.Reinspection, user }, tx);
        }
        else
        {
            claimId = claim.ClaimId;
            // UQ_qms_claim_qo is UNIQUE(quality_order_id). @qoId was inserted a
            // few statements above and cannot already carry a claim, so the move
            // cannot collide; FK_qms_claim_qo is satisfied for the same reason,
            // which is why this block stays AFTER the new order's INSERT.
            //
            // Guarded three ways inside the transaction: the claim is still the
            // one we read, it is still on the order being superseded, and it is
            // still undecided. created_at / created_by are deliberately NOT
            // touched -- the thread was opened when it was opened, and rewriting
            // that would date the conversation after its own first note.
            var claimAffected = await c.ExecuteAsync(@"
                UPDATE qms_claim
                SET    quality_order_id = @qoId,
                       claim_status     = @status,
                       last_changed_at  = SYSUTCDATETIME(),
                       last_changed_by  = @user
                WHERE  claim_id         = @claimId
                  AND  quality_order_id = @originalQoId
                  AND  decided_at IS NULL",
                new { claimId, qoId, originalQoId, status = ClaimStatus.Reinspection, user }, tx);
            if (claimAffected != 1)
                throw new InvalidOperationException(
                    "The Claim Manager decided this claim a moment ago. Reopen that decision before reinspecting.");
        }

        // The reason appears in the claim conversation, where the Quality
        // Manager is looking, rather than only in the audit log.
        await c.ExecuteAsync(@"
            INSERT INTO qms_claim_note
                (claim_id, note_text, note_kind, status_at_post, created_at, created_by, author_role)
            VALUES (@claimId, @note, @kind, @status, SYSUTCDATETIME(), @user, @role)",
            new { claimId, note = $"Sent for reinspection as {qoNo}. {reason.Trim()}",
                  kind = ClaimNoteKind.StatusChange, status = ClaimStatus.Reinspection,
                  user, role = "Admin" }, tx);

        // quality_order_id is in the diff on purpose: a claim silently changing
        // which order it belongs to is exactly what an audit log is for.
        await audit.WriteAsync(c, tx, EntityTypes.Claim, claimId, ActionCodes.Reinspected,
            oldValues: claim is null ? null
                     : new { claim_status = claim.ClaimStatus, quality_order_id = originalQoId },
            newValues: new { claim_status = ClaimStatus.Reinspection, quality_order_id = qoId,
                             reinspected_as = qoNo, reason },
            actor: user);

        // History: one row for the new order. None for the original -- its
        // status did not change, and a fabricated row would make the timeline
        // read as work that was never done.
        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history
                (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('QualityOrder', @qoId, NULL, 'Initial', @reason, SYSUTCDATETIME(), @user)",
            new { qoId, reason, user }, tx);

        await audit.WriteAsync(c, tx, EntityTypes.QualityOrder, qoId, ActionCodes.Created,
            oldValues: null,
            newValues: new { quality_order_no = qoNo, arrival_id = arrivalId,
                             status_code = "Initial", reinspection_of = originalQoId, reason },
            actor: user);

        await audit.WriteAsync(c, tx, EntityTypes.QualityOrder, originalQoId, ActionCodes.Reinspected,
            oldValues: new { superseded_at = (DateTime?)null },
            newValues: new { superseded_by = user, reinspected_as = qoNo, reason },
            actor: user);

        return (qoId, qoNo);
    }

    /// <summary>
    /// Takes back a reinspection raised by mistake -- typically Reinspect
    /// clicked where Reopen was meant. Exactly the inverse of
    /// <see cref="CreateAsync"/>: the reinspection is deleted, the original
    /// stops being superseded, and the claim goes back to where it was.
    ///
    /// Only while the reinspection is untouched: Initial or Open, nothing
    /// recorded on it (samples, header values, photos, documents, report PDFs
    /// or e-mails), and the claim still exactly as the reinspection left it.
    /// Past that, real work or a real decision would be thrown away, and the
    /// answer is to finish the reinspection instead.
    ///
    /// The claim: if the reinspection CREATED it (the original had none), it is
    /// deleted -- but only when its sole note is the "Sent for reinspection"
    /// line, so no one's comment is lost. If the reinspection MOVED it, it moves
    /// back with the status it had before, read from the Reinspected audit row,
    /// and a note in the conversation says why.
    ///
    /// Returns the original order's id and number.
    /// </summary>
    public static async Task<(long QoId, string QoNo)> UndoAsync(
        SqlConnection c, SqlTransaction tx, IAuditService audit,
        long reinspectionQoId, string reason, string user)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new InvalidOperationException(
                "Say why the reinspection is being undone — the reason is recorded in the audit log.");

        // Locked for the rest of the transaction, so an operator opening it or
        // adding a sample cannot slip in between these checks and the delete.
        var re = await c.QuerySingleOrDefaultAsync<UndoCandidate>(@"
            SELECT quality_order_id QoId,
                   quality_order_no QoNo,
                   arrival_id       ArrivalId,
                   status_code      StatusCode,
                   reinspection_of  ReinspectionOf
            FROM   qms_quality_order WITH (UPDLOCK, HOLDLOCK)
            WHERE  quality_order_id = @reinspectionQoId", new { reinspectionQoId }, tx);

        if (re is null)
            throw new InvalidOperationException("That quality order no longer exists.");
        if (re.ReinspectionOf is not { } originalQoId)
            throw new InvalidOperationException("This order is not a reinspection.");
        if (re.StatusCode != QualityOrderStatus.Initial && re.StatusCode != QualityOrderStatus.Open)
            throw new InvalidOperationException(
                $"Only a reinspection that has not been worked on can be undone — this one is {QualityOrderStatus.DisplayName(re.StatusCode)}.");

        var work = await c.ExecuteScalarAsync<int>(@"
            SELECT (SELECT COUNT(*) FROM qms_sample WHERE quality_order_id = @reinspectionQoId)
                 + (SELECT COUNT(*) FROM qms_qo_material_header_value h
                    JOIN   qms_quality_order_material m ON m.qo_material_id = h.qo_material_id
                    WHERE  m.quality_order_id = @reinspectionQoId)
                 + (SELECT COUNT(*) FROM qms_image_link
                    WHERE (owner_type = 'QualityOrder' AND owner_id = @reinspectionQoId)
                       OR (owner_type = 'QualityOrderMaterial' AND owner_id IN
                           (SELECT qo_material_id FROM qms_quality_order_material WHERE quality_order_id = @reinspectionQoId)))
                 + (SELECT COUNT(*) FROM qms_document WHERE owner_type = 'QualityOrder' AND owner_id = @reinspectionQoId)
                 + (SELECT COUNT(*) FROM qms_report_log WHERE quality_order_id = @reinspectionQoId)
                 + (SELECT COUNT(*) FROM qms_qo_report_send WHERE quality_order_id = @reinspectionQoId)",
            new { reinspectionQoId }, tx);
        if (work > 0)
            throw new InvalidOperationException(
                "Work has already been recorded on this reinspection (samples, values, photos, documents or reports), so it cannot be undone. Finish it instead.");

        var originalNo = await c.ExecuteScalarAsync<string?>(@"
            SELECT quality_order_no FROM qms_quality_order WITH (UPDLOCK, HOLDLOCK)
            WHERE  quality_order_id = @originalQoId AND superseded_at IS NOT NULL",
            new { originalQoId }, tx)
            ?? throw new InvalidOperationException("The original order is no longer superseded by this reinspection.");

        // ---- The claim ----
        var claim = await c.QuerySingleOrDefaultAsync<ClaimRow>(@"
            SELECT claim_id ClaimId, claim_status ClaimStatus, decided_at DecidedAt
            FROM   qms_claim WHERE quality_order_id = @reinspectionQoId",
            new { reinspectionQoId }, tx);

        string? restoredStatus = null;
        if (claim is not null)
        {
            if (claim.DecidedAt.HasValue || claim.ClaimStatus != ClaimStatus.Reinspection)
                throw new InvalidOperationException(
                    "The claim has moved on since the reinspection was raised, so it cannot be undone.");

            // CreateAsync wrote one Reinspected row against the claim; its old
            // values say whether the claim existed before and with what status.
            var oldJson = await c.QueryFirstOrDefaultAsync<string?>(@"
                SELECT TOP 1 old_values_json FROM qms_audit_log
                WHERE  entity_type = @entityType AND entity_id = @claimId
                  AND  action_code = @action
                  AND  (new_values_json LIKE @key + ',%' OR new_values_json LIKE @key + '}%')
                ORDER  BY audit_id DESC",
                new { entityType = EntityTypes.Claim, claimId = claim.ClaimId, action = ActionCodes.Reinspected,
                      key = $"%\"quality_order_id\":{reinspectionQoId}" }, tx);

            if (string.IsNullOrWhiteSpace(oldJson))
            {
                // Created by the reinspection. Drop it with its one automatic
                // note, unless somebody has written in the conversation since.
                var otherNotes = await c.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM qms_claim_note
                    WHERE  claim_id = @claimId AND note_text NOT LIKE @auto",
                    new { claimId = claim.ClaimId, auto = $"Sent for reinspection as {re.QoNo}.%" }, tx);
                if (otherNotes > 0)
                    throw new InvalidOperationException(
                        "Someone has already written in this reinspection's claim conversation, so it cannot be undone without losing their notes.");

                await c.ExecuteAsync("DELETE FROM qms_claim WHERE claim_id = @claimId",
                    new { claimId = claim.ClaimId }, tx);   // notes and read markers cascade
            }
            else
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(oldJson))
                    if (doc.RootElement.TryGetProperty("claim_status", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String)
                        restoredStatus = s.GetString();
                if (string.IsNullOrEmpty(restoredStatus))
                    throw new InvalidOperationException(
                        "The claim's status before the reinspection could not be found, so it cannot be moved back safely.");

                // Back onto the original, keeping its claim_id so the whole
                // conversation and every read marker follow, as on the way in.
                await c.ExecuteAsync(@"
                    UPDATE qms_claim
                    SET    quality_order_id = @originalQoId,
                           claim_status     = @restoredStatus,
                           last_changed_at  = SYSUTCDATETIME(),
                           last_changed_by  = @user
                    WHERE  claim_id = @claimId",
                    new { originalQoId, restoredStatus, user, claimId = claim.ClaimId }, tx);

                await c.ExecuteAsync(@"
                    INSERT INTO qms_claim_note
                        (claim_id, note_text, note_kind, status_at_post, created_at, created_by, author_role)
                    VALUES (@claimId, @note, @kind, @status, SYSUTCDATETIME(), @user, @role)",
                    new { claimId = claim.ClaimId,
                          note = $"Reinspection {re.QoNo} was undone; this conversation is back on {originalNo}. {reason.Trim()}",
                          kind = ClaimNoteKind.StatusChange, status = restoredStatus, user, role = "Admin" }, tx);

                await audit.WriteAsync(c, tx, EntityTypes.Claim, claim.ClaimId, ActionCodes.ReinspectionUndone,
                    oldValues: new { claim_status = ClaimStatus.Reinspection, quality_order_id = reinspectionQoId },
                    newValues: new { claim_status = restoredStatus, quality_order_id = originalQoId, reason },
                    actor: user);
            }
        }

        // ---- The orders ----
        // Audit first, while the row is still there to describe.
        await audit.WriteAsync(c, tx, EntityTypes.QualityOrder, reinspectionQoId, ActionCodes.Deleted,
            oldValues: new { quality_order_no = re.QoNo, arrival_id = re.ArrivalId, status_code = re.StatusCode,
                             reinspection_of = originalQoId },
            newValues: new { reason = $"Reinspection undone: {reason.Trim()}",
                             claim = claim is null ? "none" : restoredStatus is null ? "deleted" : "moved back" },
            actor: user);

        await QoCascade.DeleteAsync(c, tx, new[] { reinspectionQoId });

        // After the delete: UX_qms_quality_order_active_per_arrival allows one
        // live order per arrival, and until now that was the reinspection.
        await c.ExecuteAsync(@"
            UPDATE qms_quality_order
            SET    superseded_at = NULL,
                   superseded_by = NULL
            WHERE  quality_order_id = @originalQoId",
            new { originalQoId }, tx);

        await audit.WriteAsync(c, tx, EntityTypes.QualityOrder, originalQoId, ActionCodes.ReinspectionUndone,
            oldValues: new { reinspected_as = re.QoNo },
            newValues: new { superseded_at = (DateTime?)null, reason },
            actor: user);

        return (originalQoId, originalNo);
    }

    private sealed class UndoCandidate
    {
        public long   QoId           { get; set; }
        public string QoNo           { get; set; } = "";
        public long   ArrivalId      { get; set; }
        public string StatusCode     { get; set; } = "";
        public long?  ReinspectionOf { get; set; }
    }

    /// <summary>
    /// The columns the guards above need, read in one round trip.
    ///
    /// A class with settable properties rather than a record struct: Dapper
    /// maps a nullable value type by position, not by name, so
    /// QuerySingleOrDefault&lt;ReinspectCandidate?&gt; silently produced a null
    /// candidate and every guard reported "no longer exists".
    /// </summary>
    /// <summary>The original's claim, if it has one yet.</summary>
    private sealed class ClaimRow
    {
        public long      ClaimId     { get; set; }
        public string    ClaimStatus { get; set; } = "";
        public DateTime? DecidedAt   { get; set; }
    }

    private sealed class ReinspectCandidate
    {
        public long      QoId              { get; set; }
        public string    QoNo              { get; set; } = "";
        public long      ArrivalId         { get; set; }
        public string    StatusCode        { get; set; } = "";
        public bool      ContainerRejected { get; set; }
        public long?     ReinspectionOf    { get; set; }
        public DateTime? SupersededAt      { get; set; }
        public DateTime? ArchivedAt        { get; set; }
    }
}
