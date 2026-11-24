using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Survey management, stage 2: the claim step.
///
/// A finished order's claim follows its container's survey -- a confirmed
/// survey raises the claim request, no survey (or a passed one) marks the
/// order Passed QC -- and it must come out the same whichever of "finish" and
/// "decide" happens first, however often the step runs, and when both happen
/// at once. It never overrides a person: a decided claim, and a claim request
/// a manager raised by hand, are left alone.
///
/// Every test creates its own throwaway arrival and order and removes them
/// again; this suite runs against the live database.
/// </summary>
[Collection("workflow")]
public class SurveyClaimTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SurveyClaimTests(QmsAppFactory factory) => _factory = factory;

    // ---- plumbing ---------------------------------------------------

    private Task<(long ArrivalId, long? QoId, string Tag)> SeedAsync(string arrivalStatus, string? qoStatus, bool rejected = false) =>
        SurveyTestData.SeedAsync(_factory, arrivalStatus, qoStatus, rejected);

    private Task CleanupAsync(long arrivalId) => SurveyTestData.CleanupAsync(_factory, arrivalId);

    private async Task<T> With<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        using var s = _factory.Services.CreateScope();
        return await work(s.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<long> RequestAsync(long arrivalId, long? qoId = null)
    {
        var res = await With<ISurveyService, SurveyRequestResult>(svc => svc.RequestAsync(arrivalId,
            qoId.HasValue ? SurveySource.QualityOrder : SurveySource.Arrival, qoId,
            "Mould on several pallets", null, TestSurveyRecipients.RequesterName));
        Assert.True(res.Ok, res.Error);
        return res.SurveyId!.Value;
    }

    private async Task DecideAsync(long surveyId, bool confirm)
    {
        var (ok, error) = await With<ISurveyService, (bool ok, string? error)>(svc =>
            svc.DecideAsync(surveyId, confirm, confirm ? "Formal survey needed" : "Only one damaged carton", "decider"));
        Assert.True(ok, error);
    }

    /// <summary>Finishes a Submitted order the way the page does, with the
    /// finisher's claim assessment (No Potential Claim unless said otherwise).
    /// The seeded order has no samples, so the no-samples rule is bypassed
    /// with a reason.</summary>
    private async Task FinishAsync(long qoId, bool? potentialClaim = false)
    {
        var (ok, error) = await With<IQualityOrderService, (bool ok, string? error)>(svc =>
            svc.CloseAsync(qoId, "finisher", "Finished by a test", bypassNoSamples: true, potentialClaim));
        Assert.True(ok, error);
    }

    /// <summary>Reopens a finished order and submits it again, ready for a
    /// second finish.</summary>
    private async Task ReopenAndResubmitAsync(long qoId)
    {
        var reopened = await With<IQualityOrderService, (bool ok, string? error)>(svc =>
            svc.ReopenAsync(qoId, "manager", "Wrong assessment"));
        Assert.True(reopened.ok, reopened.error);
        var submitted = await With<IQualityOrderService, (bool ok, string? error)>(svc =>
            svc.SubmitAsync(qoId, "operator", bypassNoSamples: true, reason: "Resubmitted by a test"));
        Assert.True(submitted.ok, submitted.error);
    }

    private sealed class ClaimState
    {
        public string?   ClaimStatus   { get; set; }
        public string?   CreatedBy     { get; set; }
        public DateTime? DecidedAt     { get; set; }
        public bool?     PotentialClaim{ get; set; }
        public int       ClaimRows     { get; set; }
        public int       SystemNotes   { get; set; }
        public string?   LastNote      { get; set; }
    }

    private async Task<ClaimState> StateAsync(long qoId)
    {
        using var c = SurveyTestData.Db(_factory);
        return await c.QuerySingleAsync<ClaimState>(@"
            SELECT (SELECT TOP 1 claim_status FROM qms_claim WHERE quality_order_id = @qoId) AS ClaimStatus,
                   (SELECT TOP 1 created_by   FROM qms_claim WHERE quality_order_id = @qoId) AS CreatedBy,
                   (SELECT TOP 1 decided_at   FROM qms_claim WHERE quality_order_id = @qoId) AS DecidedAt,
                   (SELECT potential_claim FROM qms_quality_order WHERE quality_order_id = @qoId) AS PotentialClaim,
                   (SELECT COUNT(*) FROM qms_claim WHERE quality_order_id = @qoId) AS ClaimRows,
                   (SELECT COUNT(*) FROM qms_claim_note n JOIN qms_claim cl ON cl.claim_id = n.claim_id
                     WHERE cl.quality_order_id = @qoId AND n.created_by = 'system') AS SystemNotes,
                   (SELECT TOP 1 n.note_text FROM qms_claim_note n JOIN qms_claim cl ON cl.claim_id = n.claim_id
                     WHERE cl.quality_order_id = @qoId ORDER BY n.note_id DESC) AS LastNote",
            new { qoId });
    }

    // ---- the four combinations --------------------------------------

    [Fact]
    public async Task Finished_with_no_survey_is_marked_passed_qc()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            await FinishAsync(qoId!.Value);

            var s = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.PassedQC, s.ClaimStatus);
            Assert.Equal("system", s.CreatedBy);
            Assert.False(s.PotentialClaim);
            Assert.Equal(1, s.SystemNotes);
            Assert.Contains("no survey was requested", s.LastNote);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task Survey_confirmed_first_then_finished_raises_the_claim_request()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            var surveyId = await RequestAsync(arrivalId, qoId);
            await DecideAsync(surveyId, confirm: true);
            // Not finished yet: nothing is raised, whatever the committee does.
            Assert.Null((await StateAsync(qoId!.Value)).ClaimStatus);

            await FinishAsync(qoId.Value);

            var s = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
            Assert.True(s.PotentialClaim);
            Assert.Null(s.DecidedAt);                       // raised, never approved by this
            Assert.Contains("was confirmed by decider", s.LastNote);
            Assert.Equal(1, s.ClaimRows);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task Finished_first_while_the_survey_is_open_then_confirmed_raises_it_at_once()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            var surveyId = await RequestAsync(arrivalId, qoId);
            await FinishAsync(qoId!.Value);

            // Open survey: the order waits with no claim row, carrying the
            // finisher's own answer for now.
            var waiting = await StateAsync(qoId.Value);
            Assert.Null(waiting.ClaimStatus);
            Assert.False(waiting.PotentialClaim);

            await DecideAsync(surveyId, confirm: true);

            var s = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
            Assert.True(s.PotentialClaim);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task Finished_first_while_the_survey_is_open_then_passed_marks_passed_qc()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            var surveyId = await RequestAsync(arrivalId, qoId);
            await FinishAsync(qoId!.Value);
            await DecideAsync(surveyId, confirm: false);

            var s = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.PassedQC, s.ClaimStatus);
            Assert.False(s.PotentialClaim);
            Assert.Contains("was passed by decider", s.LastNote);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_survey_asked_for_after_an_automatic_pass_wins_when_confirmed()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            await FinishAsync(qoId!.Value);
            Assert.Equal(ClaimStatus.PassedQC, (await StateAsync(qoId.Value)).ClaimStatus);

            var surveyId = await RequestAsync(arrivalId, qoId);
            // Requested but undecided: nothing moves until somebody decides.
            var asked = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.PassedQC, asked.ClaimStatus);
            Assert.False(asked.PotentialClaim);

            await DecideAsync(surveyId, confirm: true);
            var s = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
            Assert.True(s.PotentialClaim);
            Assert.Equal(1, s.ClaimRows);
            Assert.Equal(2, s.SystemNotes);             // the automatic pass, then the automatic raise
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- the finisher's own answer ----------------------------------

    [Fact]
    public async Task A_potential_claim_from_the_finisher_stays_pending_for_a_manager()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            await FinishAsync(qoId!.Value, potentialClaim: true);

            // No survey and a potential claim: nothing is marked by itself.
            var s = await StateAsync(qoId.Value);
            Assert.Equal(0, s.ClaimRows);
            Assert.True(s.PotentialClaim);
            using (var c = SurveyTestData.Db(_factory))
                Assert.Equal("finisher", await c.ExecuteScalarAsync<string>(
                    "SELECT potential_claim_by FROM qms_quality_order WHERE quality_order_id = @qoId", new { qoId }));

            // Running the step again leaves it Pending.
            var again = await With<ISurveyClaimSync, ClaimSyncResult>(svc => svc.AfterOrderFinishedAsync(qoId.Value));
            Assert.Equal(ClaimSyncAction.None, again.Action);
            Assert.Equal(0, (await StateAsync(qoId.Value)).ClaimRows);

            // A manager raises it by hand, as before surveys.
            Assert.True((await With<IClaimService, (bool ok, string? error)>(svc =>
                svc.MarkClaimRequestAsync(qoId.Value, "Raised by hand", "manager", RoleCodes.Manager))).ok);
            Assert.Equal(ClaimStatus.ClaimRequest, (await StateAsync(qoId.Value)).ClaimStatus);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task The_finishers_potential_claim_outlives_a_passed_survey_and_a_confirmed_one_raises_it()
    {
        var (aPass, qPass, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        var (aConf, qConf, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            // Passed: "no formal survey" is not "no claim". Still Pending, still flagged.
            var passed = await RequestAsync(aPass, qPass);
            await FinishAsync(qPass!.Value, potentialClaim: true);
            Assert.Equal(0, (await StateAsync(qPass.Value)).ClaimRows);
            await DecideAsync(passed, confirm: false);
            var p = await StateAsync(qPass.Value);
            Assert.Equal(0, p.ClaimRows);
            Assert.True(p.PotentialClaim);

            // Confirmed: the claim request is raised, as for any confirmed survey.
            var confirmed = await RequestAsync(aConf, qConf);
            await FinishAsync(qConf!.Value, potentialClaim: true);
            await DecideAsync(confirmed, confirm: true);
            var k = await StateAsync(qConf.Value);
            Assert.Equal(ClaimStatus.ClaimRequest, k.ClaimStatus);
            Assert.True(k.PotentialClaim);
        }
        finally { await CleanupAsync(aPass); await CleanupAsync(aConf); }
    }

    [Fact]
    public async Task A_confirmed_survey_wins_over_a_no_and_is_the_only_finish_that_needs_no_answer()
    {
        var (aNo, qNo, _)     = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        var (aNone, qNone, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        var (aOpen, qOpen, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            Task<(bool ok, string? error)> FinishUnanswered(long qoId) => With<IQualityOrderService, (bool ok, string? error)>(svc =>
                svc.CloseAsync(qoId, "finisher", "Finished by a test", bypassNoSamples: true));
            async Task<string> StatusAsync(long qoId)
            {
                using var c = SurveyTestData.Db(_factory);
                return await c.ExecuteScalarAsync<string>(
                    "SELECT status_code FROM qms_quality_order WHERE quality_order_id = @qoId", new { qoId });
            }

            // Confirmed survey, finisher says No: still a potential claim.
            await DecideAsync(await RequestAsync(aNo, qNo), confirm: true);
            await FinishAsync(qNo!.Value, potentialClaim: false);
            var s = await StateAsync(qNo.Value);
            Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
            Assert.True(s.PotentialClaim);

            // No survey, or one still open: a finish with no answer is refused.
            var refused = await FinishUnanswered(qNone!.Value);
            Assert.False(refused.ok);
            Assert.Contains("Choose Potential Claim or No Potential Claim", refused.error);
            Assert.Equal(QualityOrderStatus.Submitted, await StatusAsync(qNone.Value));

            var open = await RequestAsync(aOpen, qOpen);
            Assert.False((await FinishUnanswered(qOpen!.Value)).ok);
            Assert.Equal(QualityOrderStatus.Submitted, await StatusAsync(qOpen.Value));

            // Once that survey is confirmed there is nothing left to answer.
            await DecideAsync(open, confirm: true);
            var accepted = await FinishUnanswered(qOpen.Value);
            Assert.True(accepted.ok, accepted.error);
            Assert.Equal(ClaimStatus.ClaimRequest, (await StateAsync(qOpen.Value)).ClaimStatus);
        }
        finally { await CleanupAsync(aNo); await CleanupAsync(aNone); await CleanupAsync(aOpen); }
    }

    [Fact]
    public async Task Finishing_again_as_a_potential_claim_takes_the_automatic_pass_back_unless_a_person_wrote_on_it()
    {
        var (aClean, qClean, _)     = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        var (aTouched, qTouched, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            // Finished as "no potential claim": passed automatically.
            await FinishAsync(qClean!.Value, potentialClaim: false);
            Assert.Equal(ClaimStatus.PassedQC, (await StateAsync(qClean.Value)).ClaimStatus);

            // Reopened and finished with the other answer: Pending again.
            await ReopenAndResubmitAsync(qClean.Value);
            await FinishAsync(qClean.Value, potentialClaim: true);
            var s = await StateAsync(qClean.Value);
            Assert.Equal(0, s.ClaimRows);
            Assert.True(s.PotentialClaim);

            // The same, but somebody has written on the claim meanwhile: it stays.
            await FinishAsync(qTouched!.Value, potentialClaim: false);
            Assert.True((await With<IClaimService, (bool ok, string? error)>(svc =>
                svc.AddNoteAsync(qTouched.Value, "Checked with the supplier", "manager", RoleCodes.Manager))).ok);
            await ReopenAndResubmitAsync(qTouched.Value);
            await FinishAsync(qTouched.Value, potentialClaim: true);
            var t = await StateAsync(qTouched.Value);
            Assert.Equal(ClaimStatus.PassedQC, t.ClaimStatus);
            Assert.True(t.PotentialClaim);
        }
        finally { await CleanupAsync(aClean); await CleanupAsync(aTouched); }
    }

    // ---- never override a person ------------------------------------

    [Fact]
    public async Task A_claim_the_claim_manager_decided_is_left_alone()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            await FinishAsync(qoId!.Value);
            Assert.True((await With<IClaimService, (bool ok, string? error)>(svc =>
                svc.MarkClaimRequestAsync(qoId.Value, "Raised by hand", "manager", RoleCodes.Manager))).ok);
            Assert.True((await With<IClaimService, (bool ok, string? error)>(svc =>
                svc.HoldAsync(qoId.Value, "Holding this one", "claimmgr", RoleCodes.ClaimManager))).ok);

            var surveyId = await RequestAsync(arrivalId, qoId);
            await DecideAsync(surveyId, confirm: true);

            var s = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.HoldClaim, s.ClaimStatus);
            Assert.NotNull(s.DecidedAt);
            // Nothing was written about it, and nothing failed.
            Assert.Equal(1, s.SystemNotes);
            Assert.Null((await With<ISurveyService, Survey?>(svc => svc.GetAsync(surveyId)))!.FollowupError);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_claim_request_raised_by_hand_survives_a_passed_survey()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            await FinishAsync(qoId!.Value);
            Assert.True((await With<IClaimService, (bool ok, string? error)>(svc =>
                svc.MarkClaimRequestAsync(qoId.Value, "Raised by hand", "manager", RoleCodes.Manager))).ok);

            var surveyId = await RequestAsync(arrivalId, qoId);
            await DecideAsync(surveyId, confirm: false);

            // Passing a survey does not take a manager's claim request away.
            Assert.Equal(ClaimStatus.ClaimRequest, (await StateAsync(qoId.Value)).ClaimStatus);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- repeating and racing ---------------------------------------

    [Fact]
    public async Task Running_the_step_again_changes_nothing()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            var surveyId = await RequestAsync(arrivalId, qoId);
            await DecideAsync(surveyId, confirm: true);
            await FinishAsync(qoId!.Value);

            for (var i = 0; i < 3; i++)
            {
                var a = await With<ISurveyClaimSync, ClaimSyncResult>(svc => svc.AfterOrderFinishedAsync(qoId.Value));
                var b = await With<ISurveyClaimSync, ClaimSyncResult>(svc => svc.AfterSurveyDecidedAsync(arrivalId));
                Assert.Equal(ClaimSyncAction.None, a.Action);
                Assert.Equal(ClaimSyncAction.None, b.Action);
                Assert.Null(await With<ISurveyService, string?>(svc => svc.RunFollowUpsAsync(surveyId, "retry")));
            }

            var s = await StateAsync(qoId.Value);
            Assert.Equal(1, s.ClaimRows);
            Assert.Equal(1, s.SystemNotes);
            Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task Confirming_and_finishing_at_the_same_moment_raises_one_claim_request()
    {
        // The race the two triggers exist for. Several rounds, because a race
        // that passes once proves little.
        for (var round = 0; round < 4; round++)
        {
            var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
            try
            {
                var surveyId = await RequestAsync(arrivalId, qoId);
                await Task.WhenAll(
                    Task.Run(() => DecideAsync(surveyId, confirm: true)),
                    Task.Run(() => FinishAsync(qoId!.Value)));

                var s = await StateAsync(qoId!.Value);
                Assert.Equal(1, s.ClaimRows);
                Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
                Assert.True(s.PotentialClaim);
                Assert.Equal(1, s.SystemNotes);
            }
            finally { await CleanupAsync(arrivalId); }
        }
    }

    // ---- the quality order ------------------------------------------

    [Fact]
    public async Task Confirming_creates_the_order_when_the_completed_arrival_has_none_and_reuses_one_that_exists()
    {
        var (aNone, _, _)     = await SeedAsync(ArrivalStatus.Completed, null);
        var (aHas, qoHas, _)  = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        try
        {
            var s1 = await RequestAsync(aNone);
            await DecideAsync(s1, confirm: true);
            var created = await With<ISurveyService, Survey?>(svc => svc.GetAsync(s1));
            Assert.NotNull(created!.QualityOrderId);
            Assert.Equal(QualityOrderStatus.Initial, created.QualityOrderStatus);
            Assert.Null(created.FollowupError);

            var s2 = await RequestAsync(aHas, qoHas);
            await DecideAsync(s2, confirm: true);
            Assert.Equal(qoHas, (await With<ISurveyService, Survey?>(svc => svc.GetAsync(s2)))!.QualityOrderId);

            using var c = SurveyTestData.Db(_factory);
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_quality_order WHERE arrival_id = @a", new { a = aNone }));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_quality_order WHERE arrival_id = @a", new { a = aHas }));
        }
        finally { await CleanupAsync(aNone); await CleanupAsync(aHas); }
    }

    [Fact]
    public async Task A_survey_passed_creates_no_order()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Completed, null);
        try
        {
            var surveyId = await RequestAsync(arrivalId);
            await DecideAsync(surveyId, confirm: false);
            Assert.Null((await With<ISurveyService, Survey?>(svc => svc.GetAsync(surveyId)))!.QualityOrderId);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_draft_arrival_gets_its_order_when_it_is_completed()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var surveyId = await RequestAsync(arrivalId);
            await DecideAsync(surveyId, confirm: true);

            // Confirmed on a draft: no order can exist yet, and that is not a failure.
            var draft = await With<ISurveyService, Survey?>(svc => svc.GetAsync(surveyId));
            Assert.Null(draft!.QualityOrderId);
            Assert.Null(draft.FollowupError);
            Assert.Null(await With<ISurveyService, long?>(svc => svc.AfterArrivalCompletedAsync(arrivalId, "test")));

            using (var c = SurveyTestData.Db(_factory))
                await c.ExecuteAsync("UPDATE qms_arrival SET status_code = 'Completed' WHERE arrival_id = @arrivalId", new { arrivalId });

            var qoId = await With<ISurveyService, long?>(svc => svc.AfterArrivalCompletedAsync(arrivalId, "test"));
            Assert.NotNull(qoId);
            // Asked again, it creates nothing more.
            Assert.Null(await With<ISurveyService, long?>(svc => svc.AfterArrivalCompletedAsync(arrivalId, "test")));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- special orders ---------------------------------------------

    [Fact]
    public async Task A_rejected_container_stays_pending_until_its_survey_is_confirmed()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Rejected, QualityOrderStatus.Closed, rejected: true);
        try
        {
            // No survey: a rejected container is never passed automatically.
            var none = await With<ISurveyClaimSync, ClaimSyncResult>(svc => svc.AfterSurveyDecidedAsync(arrivalId));
            Assert.Equal(ClaimSyncAction.None, none.Action);
            var before = await StateAsync(qoId!.Value);
            Assert.Null(before.ClaimStatus);
            Assert.True(before.PotentialClaim);

            var surveyId = await RequestAsync(arrivalId, qoId);
            await DecideAsync(surveyId, confirm: true);

            var s = await StateAsync(qoId.Value);
            Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
            Assert.True(s.PotentialClaim);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task An_archived_order_is_not_touched()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Closed);
        try
        {
            var surveyId = await RequestAsync(arrivalId, qoId);
            using (var c = SurveyTestData.Db(_factory))
                await c.ExecuteAsync(
                    "UPDATE qms_quality_order SET archived_at = SYSUTCDATETIME(), archived_by = 'test' WHERE quality_order_id = @qoId",
                    new { qoId });

            await DecideAsync(surveyId, confirm: true);
            Assert.Null((await StateAsync(qoId!.Value)).ClaimStatus);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task Reopening_and_finishing_again_keeps_one_claim()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            var surveyId = await RequestAsync(arrivalId, qoId);
            await DecideAsync(surveyId, confirm: true);
            await FinishAsync(qoId!.Value);

            Assert.True((await With<IQualityOrderService, (bool ok, string? error)>(svc =>
                svc.ReopenAsync(qoId.Value, "manager", "Wrong reading entered"))).ok);
            using (var c = SurveyTestData.Db(_factory))
                await c.ExecuteAsync(
                    "UPDATE qms_quality_order SET status_code = 'Submitted' WHERE quality_order_id = @qoId", new { qoId });
            await FinishAsync(qoId.Value);

            var s = await StateAsync(qoId.Value);
            Assert.Equal(1, s.ClaimRows);
            Assert.Equal(ClaimStatus.ClaimRequest, s.ClaimStatus);
            Assert.Equal(1, s.SystemNotes);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- the Finish dialog and Retry --------------------------------

    private HttpClient Admin()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, RoleCodes.Admin);
        return c;
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token on " + url);
        return m.Groups[1].Value;
    }

    [Fact]
    public async Task The_finish_dialog_asks_for_a_claim_assessment_unless_the_survey_is_confirmed()
    {
        var (arrivalId, qoId, _)   = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        var (aConf, qConf, _)      = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Submitted);
        try
        {
            var admin = Admin();
            async Task<HttpResponseMessage> FinishAsync(long id, string? claim) =>
                await admin.PostAsync("/QualityOrders/Close/" + id, new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, "/QualityOrders/Details/" + id)),
                    new KeyValuePair<string, string>("reason", "Finished from the dialog"),
                    new KeyValuePair<string, string>("bypassNoSamples", "true"),
                }.Concat(claim == null
                    ? Array.Empty<KeyValuePair<string, string>>()
                    : new[] { new KeyValuePair<string, string>("potentialClaim", claim) })));
            async Task<string> StatusAsync(long id)
            {
                using var c = SurveyTestData.Db(_factory);
                return await c.ExecuteScalarAsync<string>(
                    "SELECT status_code FROM qms_quality_order WHERE quality_order_id = @id", new { id });
            }

            // No survey: the dialog asks, and says what each answer does.
            var page = await admin.GetStringAsync("/QualityOrders/Details/" + qoId);
            Assert.Contains("name=\"potentialClaim\"", page);
            Assert.Contains("Potential Claim leaves the order Pending", page);

            // A post without the answer finishes nothing.
            Assert.Equal(HttpStatusCode.Redirect, (await FinishAsync(qoId!.Value, null)).StatusCode);
            Assert.Equal(QualityOrderStatus.Submitted, await StatusAsync(qoId.Value));

            // Potential Claim: finished, flagged, and left Pending for a manager.
            Assert.Equal(HttpStatusCode.Redirect, (await FinishAsync(qoId.Value, "Yes")).StatusCode);
            Assert.Equal(QualityOrderStatus.Closed, await StatusAsync(qoId.Value));
            var s = await StateAsync(qoId.Value);
            Assert.Equal(0, s.ClaimRows);
            Assert.True(s.PotentialClaim);

            // Confirmed survey: nothing to pick, and a post without it is accepted.
            await DecideAsync(await RequestAsync(aConf, qConf), confirm: true);
            var confirmedPage = await admin.GetStringAsync("/QualityOrders/Details/" + qConf);
            Assert.DoesNotContain("name=\"potentialClaim\"", confirmedPage);
            Assert.Contains("a claim notification request will be raised automatically", confirmedPage);
            Assert.Equal(HttpStatusCode.Redirect, (await FinishAsync(qConf!.Value, null)).StatusCode);
            Assert.Equal(QualityOrderStatus.Closed, await StatusAsync(qConf.Value));
            Assert.Equal(ClaimStatus.ClaimRequest, (await StateAsync(qConf.Value)).ClaimStatus);
        }
        finally { await CleanupAsync(arrivalId); await CleanupAsync(aConf); }
    }

    [Fact]
    public async Task Retry_clears_a_recorded_failure_once_the_steps_succeed()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Closed);
        try
        {
            var surveyId = await RequestAsync(arrivalId, qoId);
            await DecideAsync(surveyId, confirm: true);

            // As if the claim step had failed after the decision was saved.
            using (var c = SurveyTestData.Db(_factory))
            {
                await c.ExecuteAsync("DELETE FROM qms_claim_note WHERE claim_id IN (SELECT claim_id FROM qms_claim WHERE quality_order_id = @qoId)", new { qoId });
                await c.ExecuteAsync("DELETE FROM qms_claim WHERE quality_order_id = @qoId", new { qoId });
                await c.ExecuteAsync(
                    "UPDATE qms_survey SET followup_error = 'The claim step could not be completed: test', followup_failed_at = SYSUTCDATETIME() WHERE survey_id = @surveyId",
                    new { surveyId });
            }

            var admin = Admin();
            var url = "/Surveys/Details/" + surveyId;
            Assert.Contains("A step after the decision did not complete", await admin.GetStringAsync(url));

            var res = await admin.PostAsync("/Surveys/RetryFollowUp/" + surveyId, new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, url)),
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

            var after = await With<ISurveyService, Survey?>(svc => svc.GetAsync(surveyId));
            Assert.Null(after!.FollowupError);
            Assert.Equal(ClaimStatus.ClaimRequest, after.ClaimStatus);
            Assert.DoesNotContain("A step after the decision did not complete", await admin.GetStringAsync(url));
        }
        finally { await CleanupAsync(arrivalId); }
    }
}
