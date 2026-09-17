using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The quality order raised when a container is refused on arrival.
///
/// Static and transaction-scoped, like <see cref="QoCascade"/>, so the whole
/// rejection — arrival status, snapshot freeze, order creation, history and
/// audit — commits or rolls back as one unit. A rejection that left an arrival
/// refused with no claim order behind it would lose the claim.
/// </summary>
public static class RejectionOrder
{
    /// <summary>
    /// Creates the order ALREADY CLOSED, carrying a potential claim.
    ///
    /// Deliberately NOT routed through QualityOrderService.Transition. That path
    /// enforces "every material has a sample" and "every sample has a photo",
    /// and a rejection order has neither — so it would pass both checks only
    /// VACUOUSLY, because zero rows means zero violations. Depending on a
    /// vacuous truth means the day somebody tightens either rule ("an order must
    /// have at least one sample") this flow breaks silently, at the worst
    /// possible moment: a damaged container with a claim window running. A
    /// direct INSERT states the intent and cannot be undermined that way.
    ///
    /// Returns the new order's id and number.
    /// </summary>
    public static async Task<(long QoId, string QoNo)> CreateClosedAsync(
        SqlConnection c, SqlTransaction tx, IAuditService audit,
        long arrivalId, string reason, string user)
    {
        // Re-checked inside the transaction, not just at the controller: a
        // Cancelled order can exist on a Draft arrival after an admin cancel,
        // and two concurrent rejections must not both win.
        var existing = await c.QuerySingleOrDefaultAsync<long?>(
            "SELECT quality_order_id FROM qms_quality_order " +
            "WHERE arrival_id=@arrivalId AND status_code <> 'Cancelled' AND superseded_at IS NULL",
            new { arrivalId }, tx);
        if (existing.HasValue)
            throw new InvalidOperationException(
                $"This arrival already has an active Quality Order (#{existing.Value}); it cannot be rejected.");

        var seq = await c.ExecuteScalarAsync<int>(
            "SELECT NEXT VALUE FOR seq_qms_quality_order_no", transaction: tx);
        var qoNo = $"QO-{DateTime.UtcNow:yyyy}-{seq:D6}";

        // One INSERT lands the order finished. opened_at is stamped as well as
        // closed_at because the report prints it as the Inspection Date and
        // would otherwise fall back to created_at a few milliseconds apart,
        // showing two timestamps for one event.
        var qoId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_quality_order
                (quality_order_no, arrival_id, status_code, container_rejected,
                 created_at, created_by,
                 opened_at, opened_by,
                 closed_at, closed_by, close_reason,
                 potential_claim, potential_claim_at, potential_claim_by)
            VALUES (@qoNo, @arrivalId, 'Closed', 1,
                 SYSUTCDATETIME(), @user,
                 SYSUTCDATETIME(), @user,
                 SYSUTCDATETIME(), @user, @reason,
                 1, SYSUTCDATETIME(), @user);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { qoNo, arrivalId, user, reason }, tx);

        // Materials still come across. The supplier's claim has to say WHAT was
        // in the container even though none of it was sampled.
        await c.ExecuteAsync(@"
            INSERT INTO qms_quality_order_material
                (quality_order_id, arrival_item_id, material_no, material_desc,
                 material_group, material_group_desc, major_category)
            SELECT @qoId, ai.arrival_item_id, ai.material_no, ai.material_desc,
                   ai.material_group, ai.material_group_desc, ai.major_category
            FROM   qms_arrival_item ai
            WHERE  ai.arrival_id = @arrivalId;",
            new { qoId, arrivalId }, tx);

        // One history row, NULL -> Closed. Every other order's chain has each
        // old_status matching its predecessor's new_status; this one has no
        // predecessor because no intermediate state ever existed. Fabricating
        // Initial/Open/Submitted rows would make the timeline read as work that
        // was never done.
        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('QualityOrder', @qoId, NULL, 'Closed', @reason, SYSUTCDATETIME(), @user)",
            new { qoId, user, reason }, tx);

        await audit.WriteAsync(c, tx,
            EntityTypes.QualityOrder, qoId, ActionCodes.Created,
            oldValues: null,
            newValues: new
            {
                quality_order_no   = qoNo,
                arrival_id         = arrivalId,
                status_code        = "Closed",
                container_rejected = true,
                potential_claim    = "Potential Claim",
                reason
            },
            actor: user);

        return (qoId, qoNo);
    }

    /// <summary>
    /// Cancels a rejection order so the arrival can go back to Draft.
    ///
    /// This exists because nothing else can do it: CancelAsync accepts only
    /// Initial/Open/Submitted and Delete only Initial/Open, so a Closed
    /// rejection order is otherwise permanent. Without this a mis-click on a
    /// container that turns out to be fine would need a DBA.
    /// </summary>
    public static async Task<int> CancelAsync(
        SqlConnection c, SqlTransaction tx, IAuditService audit,
        long arrivalId, string reason, string user)
    {
        var qo = await c.QuerySingleOrDefaultAsync<(long Id, string No)?>(
            @"SELECT quality_order_id, quality_order_no
              FROM   qms_quality_order
              WHERE  arrival_id = @arrivalId AND container_rejected = 1 AND status_code = 'Closed'
             AND    superseded_at IS NULL",
            new { arrivalId }, tx);
        if (qo is null) return 0;

        var affected = await c.ExecuteAsync(@"
            UPDATE qms_quality_order
            SET    status_code = 'Cancelled', close_reason = @reason
            WHERE  quality_order_id = @qoId AND status_code = 'Closed'",
            new { qoId = qo.Value.Id, reason }, tx);

        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('QualityOrder', @qoId, 'Closed', 'Cancelled', @reason, SYSUTCDATETIME(), @user)",
            new { qoId = qo.Value.Id, reason, user }, tx);

        await audit.WriteAsync(c, tx,
            EntityTypes.QualityOrder, qo.Value.Id, ActionCodes.Cancelled,
            oldValues: new { status_code = "Closed" },
            newValues: new { status_code = "Cancelled", reason, quality_order_no = qo.Value.No },
            actor: user);

        return affected;
    }
}
