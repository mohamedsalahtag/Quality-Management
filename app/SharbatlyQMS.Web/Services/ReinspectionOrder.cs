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
/// materials, the supersede stamp on the original, the claim decision, history
/// and audit — commits or rolls back as one unit. A half-applied reinspection
/// would leave a container with two live orders, or an original superseded by
/// an order that was never created.
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

        // The decision itself is a claim decision, recorded on the ORIGINAL
        // order's claim so the worklist says why the container came back
        // rather than the row simply vanishing when the original is
        // superseded.
        //
        // The Claim Manager's lock-out is honoured rather than bypassed: once
        // they have decided, a Quality Manager cannot change the status, and
        // neither can this. Refusing here is the same rule, not a new one --
        // silently leaving the claim untouched would supersede an order whose
        // settled verdict no longer matches the inspection behind it.
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
            claimId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_claim
                    (quality_order_id, claim_status, created_at, created_by,
                     last_changed_at, last_changed_by)
                VALUES (@originalQoId, @status, SYSUTCDATETIME(), @user, SYSUTCDATETIME(), @user);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { originalQoId, status = ClaimStatus.Reinspection, user }, tx);
        }
        else
        {
            claimId = claim.ClaimId;
            // Guarded the same way the Quality Manager's own writes are: if a
            // decision lands between the read above and this update, nothing
            // is overwritten.
            var claimAffected = await c.ExecuteAsync(@"
                UPDATE qms_claim
                SET    claim_status    = @status,
                       last_changed_at = SYSUTCDATETIME(),
                       last_changed_by = @user
                WHERE  claim_id = @claimId AND decided_at IS NULL",
                new { claimId, status = ClaimStatus.Reinspection, user }, tx);
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

        await audit.WriteAsync(c, tx, EntityTypes.Claim, claimId, ActionCodes.Reinspected,
            oldValues: claim is null ? null : new { claim_status = claim.ClaimStatus },
            newValues: new { claim_status = ClaimStatus.Reinspection, reinspected_as = qoNo, reason },
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
