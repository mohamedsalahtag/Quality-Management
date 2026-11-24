using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>What the claim step did to an order's claim.</summary>
public enum ClaimSyncAction
{
    /// <summary>Nothing to do: not finished, archived, a survey still open, or
    /// a claim a person has already set.</summary>
    None,
    RaisedClaimRequest,
    MarkedPassedQc
}

public record ClaimSyncResult(ClaimSyncAction Action, long? QualityOrderId, string? Error)
{
    public bool Failed => Error != null;
}

/// <summary>
/// The claim step: the ONE place that decides what a finished order's claim
/// should be, given its container's survey and the finisher's own answer in
/// the Finish dialog. It runs after each of the two events that can make the
/// answer change -- an order being finished, and a survey being decided -- so
/// it does not matter which happens first.
///
///   survey Confirmed                         -> Claim Notification Request
///   survey still Open                        -> nothing yet; settled when decided
///   no survey, or survey Passed:
///     finisher chose Potential Claim         -> nothing: the order stays Pending
///                                               for a manager, as before surveys
///     finisher chose No Potential Claim      -> Passed QC
///
/// The finisher's choice was taken out of the Finish dialog with M45 and put
/// back on 2026-11-24: some orders must be raised as potential claims without
/// a survey. A confirmed survey still wins over a "No" from the finisher.
///
/// It never overrides a person. A claim the Claim Manager has decided is left
/// alone, and so is a claim request a manager raised by hand on an order with
/// no confirmed survey. The only thing it changes is an automatic or
/// not-yet-decided marking: Passed QC (or "awaiting decision" after a
/// reinspection) becomes a claim request once the survey is confirmed.
///
/// Rejected containers are not auto-passed -- they carry a potential claim by
/// definition and stay Pending for a manager, as before. A confirmed survey
/// still raises their claim request.
///
/// Safe to repeat and safe to race: one claim per order is a database key
/// (UQ_qms_claim_qo), the claim row is read with an update lock, and the loser
/// of an insert race simply runs again and finds the winner's row.
///
/// Runs in its own transaction AFTER the finish or the decision has
/// committed, and never throws: a failure is logged and, when the container
/// has a survey, recorded on it for the Retry button. The finish and the
/// decision stand either way.
/// </summary>
public interface ISurveyClaimSync
{
    Task<ClaimSyncResult> AfterOrderFinishedAsync(long qualityOrderId);
    Task<ClaimSyncResult> AfterSurveyDecidedAsync(long arrivalId);
}

public class SurveyClaimSync : ISurveyClaimSync
{
    /// <summary>Written as the author of automatic claim rows and notes. The
    /// KPI report already leaves this name out of per-user figures.</summary>
    public const string SystemActor = "system";
    public const string SystemRole  = "System";

    private readonly string _cs;
    private readonly IAuditService _audit;
    private readonly ISurveyNotifier _notifier;
    private readonly ILogger<SurveyClaimSync> _log;

    public SurveyClaimSync(IConfiguration config, IAuditService audit, ISurveyNotifier notifier,
        ILogger<SurveyClaimSync> log)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
        _notifier = notifier;
        _log = log;
    }

    /// <summary>
    /// The order's potential-claim flag. A confirmed survey makes it a
    /// potential claim whatever anybody chose; otherwise it is the finisher's
    /// own answer. With no answer at all (an order finished while the dialog
    /// did not ask) it is "no", or "not classified" while a survey is open.
    ///
    /// Used inside the Finish transaction, so a finished order is never
    /// missing it, and by the claim step when a survey is decided later --
    /// where <paramref name="answer"/> is the flag the order already carries.
    /// </summary>
    public static bool? PotentialClaimFor(string? surveyStatus, bool? answer) =>
        surveyStatus == SurveyStatus.Confirm
            ? true
            : answer ?? (surveyStatus == SurveyStatus.Open ? null : false);

    public Task<ClaimSyncResult> AfterOrderFinishedAsync(long qualityOrderId) =>
        RunAsync(qualityOrderId, null);

    public Task<ClaimSyncResult> AfterSurveyDecidedAsync(long arrivalId) =>
        RunAsync(null, arrivalId);

    private sealed class Ctx
    {
        public long      QoId              { get; set; }
        public string    QoNo              { get; set; } = "";
        public long      ArrivalId         { get; set; }
        public string    StatusCode        { get; set; } = "";
        public DateTime? ArchivedAt        { get; set; }
        public DateTime? SupersededAt      { get; set; }
        public bool      ContainerRejected { get; set; }
        public bool?     PotentialClaim    { get; set; }
        public string?   ClosedBy          { get; set; }
        public long?     SurveyId          { get; set; }
        public string?   SurveyNo          { get; set; }
        public string?   SurveyStatus      { get; set; }
        public string?   DecidedBy         { get; set; }
        public string?   DecisionComments  { get; set; }
    }

    private sealed class ClaimRow
    {
        public long      ClaimId       { get; set; }
        public string    ClaimStatus   { get; set; } = "";
        public DateTime? DecidedAt     { get; set; }
        public string?   LastChangedBy { get; set; }
    }

    private async Task<ClaimSyncResult> RunAsync(long? qoId, long? arrivalId)
    {
        long? surveyId = null;
        try
        {
            // One retry: the only expected failure is losing the race to insert
            // the order's claim row, and the second pass finds the winner's.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var (result, sid) = await ApplyAsync(qoId, arrivalId);
                    surveyId = sid;
                    if (sid.HasValue) await ClearErrorAsync(sid.Value);
                    return result;
                }
                catch (SqlException ex) when (ex.Number is 2601 or 2627 && attempt == 1)
                {
                    // fall through to the second pass
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Claim step failed for quality order {QoId} / arrival {ArrivalId}", qoId, arrivalId);
            var message = "The claim step could not be completed: " + ex.Message;
            await RecordErrorAsync(surveyId, qoId, arrivalId, message);
            return new ClaimSyncResult(ClaimSyncAction.None, qoId, message);
        }
    }

    private async Task<(ClaimSyncResult result, long? surveyId)> ApplyAsync(long? qoId, long? arrivalId)
    {
        using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // By order: that order. By arrival: the arrival's LIVE order (not
        // cancelled, not superseded) -- the one that speaks for the container.
        var ctx = await c.QuerySingleOrDefaultAsync<Ctx>(@"
            SELECT TOP 1
                   qo.quality_order_id   AS QoId,
                   qo.quality_order_no   AS QoNo,
                   qo.arrival_id         AS ArrivalId,
                   qo.status_code        AS StatusCode,
                   qo.archived_at        AS ArchivedAt,
                   qo.superseded_at      AS SupersededAt,
                   ISNULL(qo.container_rejected, 0) AS ContainerRejected,
                   qo.potential_claim    AS PotentialClaim,
                   qo.closed_by          AS ClosedBy,
                   s.survey_id           AS SurveyId,
                   s.survey_no           AS SurveyNo,
                   s.status_code         AS SurveyStatus,
                   s.decided_by          AS DecidedBy,
                   s.decision_comments   AS DecisionComments
            FROM   qms_quality_order qo
            LEFT   JOIN qms_survey s ON s.arrival_id = qo.arrival_id
            WHERE  (@qoId IS NOT NULL AND qo.quality_order_id = @qoId)
               OR  (@qoId IS NULL AND qo.arrival_id = @arrivalId
                    AND qo.status_code <> 'Cancelled' AND qo.superseded_at IS NULL)
            ORDER  BY qo.quality_order_id DESC",
            new { qoId, arrivalId }, tx);

        // Only a finished, live, unarchived order has a claim to settle.
        if (ctx == null || ctx.StatusCode != QualityOrderStatus.Closed
            || ctx.ArchivedAt.HasValue || ctx.SupersededAt.HasValue)
        {
            tx.Rollback();
            return (new ClaimSyncResult(ClaimSyncAction.None, ctx?.QoId, null), ctx?.SurveyId);
        }

        // ---- The potential-claim flag: the finisher's answer, which a
        //      confirmed survey overrides. A rejected container keeps the flag
        //      it was created with.
        var flag = ctx.ContainerRejected
            ? ctx.PotentialClaim
            : PotentialClaimFor(ctx.SurveyStatus, ctx.PotentialClaim);
        if (!ctx.ContainerRejected && flag != ctx.PotentialClaim)
        {
            var by = ctx.SurveyStatus is SurveyStatus.Confirm or SurveyStatus.Pass
                ? (ctx.DecidedBy ?? SystemActor)
                : (ctx.ClosedBy ?? SystemActor);
            await c.ExecuteAsync(@"
                UPDATE qms_quality_order
                SET    potential_claim    = @flag,
                       potential_claim_at = CASE WHEN @flag IS NULL THEN NULL ELSE SYSUTCDATETIME() END,
                       potential_claim_by = CASE WHEN @flag IS NULL THEN NULL ELSE @by END
                WHERE  quality_order_id = @id",
                new { flag, by, id = ctx.QoId }, tx);
            await _audit.WriteAsync(c, tx,
                EntityTypes.QualityOrder, ctx.QoId, ActionCodes.Updated,
                oldValues: new { potential_claim = ctx.PotentialClaim },
                newValues: new { potential_claim = flag },
                actor: SystemActor);
        }

        // ---- The claim row.
        // A potential claim with no confirmed survey behind it -- the
        // finisher's own call, or a rejected container -- is for a manager to
        // judge: the order stays Pending, exactly as before surveys existed.
        string? target = ctx.SurveyStatus switch
        {
            SurveyStatus.Confirm => ClaimStatus.ClaimRequest,
            SurveyStatus.Open    => null,
            _                    => flag == true ? null : ClaimStatus.PassedQC
        };
        if (target == null)
        {
            if (flag == true) await WithdrawAutomaticPassAsync(c, tx, ctx.QoId);
            tx.Commit();
            return (new ClaimSyncResult(ClaimSyncAction.None, ctx.QoId, null), ctx.SurveyId);
        }

        var claim = await ReadClaimAsync(c, tx, ctx.QoId);

        long claimId;
        string? oldStatus = null;
        if (claim == null)
        {
            claimId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_claim
                    (quality_order_id, claim_status, created_at, created_by, last_changed_at, last_changed_by)
                VALUES (@id, @target, SYSUTCDATETIME(), @actor, SYSUTCDATETIME(), @actor);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { id = ctx.QoId, target, actor = SystemActor }, tx);
        }
        else
        {
            // What may be changed: nothing decided, and only a marking that
            // is not a person's considered claim request.
            //   to Claim Request: from Passed QC, or awaiting decision after a reinspection
            //   to Passed QC:     only from awaiting decision after a reinspection
            var changeable = claim.DecidedAt == null && (
                (target == ClaimStatus.ClaimRequest &&
                    claim.ClaimStatus is ClaimStatus.PassedQC or ClaimStatus.Reinspection)
                || (target == ClaimStatus.PassedQC && claim.ClaimStatus == ClaimStatus.Reinspection));
            if (!changeable)
            {
                tx.Commit();
                return (new ClaimSyncResult(ClaimSyncAction.None, ctx.QoId, null), ctx.SurveyId);
            }
            var changed = await c.ExecuteAsync(@"
                UPDATE qms_claim
                SET    claim_status = @target, last_changed_at = SYSUTCDATETIME(), last_changed_by = @actor
                WHERE  claim_id = @claimId AND decided_at IS NULL AND claim_status = @expected",
                new { target, actor = SystemActor, claimId = claim.ClaimId, expected = claim.ClaimStatus }, tx);
            if (changed != 1)
            {
                // Decided or changed between the read and the write: leave it.
                tx.Commit();
                return (new ClaimSyncResult(ClaimSyncAction.None, ctx.QoId, null), ctx.SurveyId);
            }
            claimId   = claim.ClaimId;
            oldStatus = claim.ClaimStatus;
        }

        var note = NoteFor(ctx, target);
        await c.ExecuteAsync(@"
            INSERT INTO qms_claim_note
                (claim_id, note_text, note_kind, status_at_post, created_at, created_by, author_role)
            VALUES (@claimId, @note, @kind, @target, SYSUTCDATETIME(), @actor, @role)",
            new { claimId, note, kind = ClaimNoteKind.StatusChange, target, actor = SystemActor, role = SystemRole }, tx);

        await _audit.WriteAsync(c, tx,
            EntityTypes.Claim, claimId,
            target == ClaimStatus.ClaimRequest ? ActionCodes.ClaimRequest : ActionCodes.PassedQC,
            oldValues: oldStatus == null ? null : new { claim_status = oldStatus },
            newValues: new { claim_status = target, note },
            actor: SystemActor);

        // A claim request only ever comes from a confirmed survey here, so
        // there is always a survey to hang the notice on.
        if (target == ClaimStatus.ClaimRequest && ctx.SurveyId.HasValue)
            await _notifier.ClaimRaisedAsync(c, tx, ctx.SurveyId.Value);

        tx.Commit();
        return (new ClaimSyncResult(
            target == ClaimStatus.ClaimRequest ? ClaimSyncAction.RaisedClaimRequest : ClaimSyncAction.MarkedPassedQc,
            ctx.QoId, null), ctx.SurveyId);
    }

    private static Task<ClaimRow?> ReadClaimAsync(SqlConnection c, SqlTransaction tx, long qoId) =>
        c.QuerySingleOrDefaultAsync<ClaimRow>(@"
            SELECT claim_id AS ClaimId, claim_status AS ClaimStatus, decided_at AS DecidedAt,
                   last_changed_by AS LastChangedBy
            FROM   qms_claim WITH (UPDLOCK, HOLDLOCK)
            WHERE  quality_order_id = @qoId", new { qoId }, tx)!;

    /// <summary>
    /// An order the claim step marked Passed QC by itself is now a potential
    /// claim: it was finished as "no potential claim", reopened, and finished
    /// again with the other answer. The automatic marking spoke for the first
    /// answer only, so it is taken back and the order is Pending again.
    ///
    /// Only while nobody has touched the claim: not decided, still the
    /// system's own Passed QC, and no note written by a person. Anything a
    /// person did stays.
    /// </summary>
    private async Task WithdrawAutomaticPassAsync(SqlConnection c, SqlTransaction tx, long qoId)
    {
        var claim = await ReadClaimAsync(c, tx, qoId);
        if (claim == null || claim.DecidedAt != null || claim.ClaimStatus != ClaimStatus.PassedQC
            || claim.LastChangedBy != SystemActor)
            return;
        var claimId = claim.ClaimId;
        if (await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_claim_note WHERE claim_id = @claimId AND created_by <> @actor",
                new { claimId, actor = SystemActor }, tx) > 0)
            return;

        await c.ExecuteAsync(@"
            DELETE FROM qms_claim_read_marker WHERE claim_id = @claimId;
            DELETE FROM qms_claim_note        WHERE claim_id = @claimId;
            DELETE FROM qms_claim             WHERE claim_id = @claimId;", new { claimId }, tx);
        await _audit.WriteAsync(c, tx,
            EntityTypes.Claim, claimId, ActionCodes.Deleted,
            oldValues: new { claim_status = ClaimStatus.PassedQC },
            newValues: new { note = "Automatic Passed QC withdrawn: the order was finished again as a potential claim." },
            actor: SystemActor);
    }

    private static string NoteFor(Ctx ctx, string target)
    {
        if (target == ClaimStatus.ClaimRequest)
            return $"Raised automatically: survey {ctx.SurveyNo} was confirmed by {ctx.DecidedBy}."
                 + (string.IsNullOrWhiteSpace(ctx.DecisionComments) ? "" : " " + ctx.DecisionComments!.Trim());
        if (ctx.SurveyStatus == SurveyStatus.Pass)
            return $"Passed QC automatically: survey {ctx.SurveyNo} was passed by {ctx.DecidedBy}."
                 + (string.IsNullOrWhiteSpace(ctx.DecisionComments) ? "" : " Reason: " + ctx.DecisionComments!.Trim());
        return "Passed QC automatically: finished as No Potential Claim, and no survey was requested for this container.";
    }

    // ---- Failure bookkeeping (best effort; never throws) --------------

    private async Task RecordErrorAsync(long? surveyId, long? qoId, long? arrivalId, string message)
    {
        try
        {
            using var c = new SqlConnection(_cs);
            if (message.Length > 1000) message = message[..1000];
            await c.ExecuteAsync(@"
                UPDATE s
                SET    s.followup_error = @message, s.followup_failed_at = SYSUTCDATETIME()
                FROM   qms_survey s
                WHERE  s.survey_id = @surveyId
                   OR  (@surveyId IS NULL AND s.arrival_id = COALESCE(@arrivalId,
                          (SELECT arrival_id FROM qms_quality_order WHERE quality_order_id = @qoId)))",
                new { message, surveyId, arrivalId, qoId });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not record the claim step failure on the survey");
        }
    }

    private async Task ClearErrorAsync(long surveyId)
    {
        try
        {
            using var c = new SqlConnection(_cs);
            await c.ExecuteAsync(@"
                UPDATE qms_survey SET followup_error = NULL, followup_failed_at = NULL
                WHERE  survey_id = @surveyId AND followup_error IS NOT NULL
                  AND  followup_error LIKE 'The claim step%'", new { surveyId });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not clear the claim step failure on survey {SurveyId}", surveyId);
        }
    }
}
