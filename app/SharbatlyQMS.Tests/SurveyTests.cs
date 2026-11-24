using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Pdf;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Survey management, stage 1: asking for a survey from the arrival or the
/// quality order, the one-survey-per-arrival rule, Confirm / Pass, who may do
/// which, the UNDER SURVEY banner, and the promise that nothing about a survey
/// reaches the supplier's QC report.
///
/// Every test creates its own throwaway arrival (and order) and removes them
/// again; this suite runs against the live database.
/// </summary>
[Collection("workflow")]
public class SurveyTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SurveyTests(QmsAppFactory factory) => _factory = factory;

    // ---- plumbing ---------------------------------------------------

    private SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new SqlConnection(cfg.GetConnectionString("Default"));
    }

    private HttpClient Client(string role)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        return c;
    }

    /// <summary>An arrival with one line and a shipment snapshot, optionally
    /// carrying one quality order in the given status.</summary>
    private async Task<(long ArrivalId, long? QoId, string Tag)> SeedAsync(string arrivalStatus, string? qoStatus)
    {
        var tag = $"TESTSV{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();
        using var c = Db();
        await c.OpenAsync();

        var arrivalId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_arrival
                (arrival_no, source_system, bol_no, container_no, ebeln,
                 vendor_no, vendor_name, plant, status_code, created_at, created_by)
            VALUES (@tag, 'TEST', @tag, @tag, '4700000000',
                 'V1', 'Test vendor', 'JD01', @arrivalStatus, SYSUTCDATETIME(), 'test');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", new { tag, arrivalStatus });

        await c.ExecuteAsync(@"
            INSERT INTO qms_arrival_item (arrival_id, ebeln, ebelp, material_no, material_desc, material_group)
            VALUES (@arrivalId, '4700000000', '00010', 'TESTMAT', 'Test material', 'APPLE')",
            new { arrivalId });

        await c.ExecuteAsync(@"
            INSERT INTO qms_shipment_snapshot (arrival_id, internal_shipment_no, status_code, arrival_date)
            VALUES (@arrivalId, @tag, 'Confirmed', CAST(SYSUTCDATETIME() AS DATE))",
            new { arrivalId, tag });

        long? qoId = null;
        if (qoStatus != null)
        {
            qoId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_quality_order
                    (quality_order_no, arrival_id, status_code, created_at, created_by, opened_at, opened_by)
                VALUES (@no, @arrivalId, @qoStatus, SYSUTCDATETIME(), 'test', SYSUTCDATETIME(), 'test');
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { no = "QO-TEST-" + tag, arrivalId, qoStatus });
            await c.ExecuteAsync(@"
                INSERT INTO qms_quality_order_material
                    (quality_order_id, arrival_item_id, material_no, material_desc, material_group)
                SELECT @qoId, ai.arrival_item_id, ai.material_no, ai.material_desc, ai.material_group
                FROM   qms_arrival_item ai WHERE ai.arrival_id = @arrivalId",
                new { qoId, arrivalId });
        }
        return (arrivalId, qoId, tag);
    }

    private async Task CleanupAsync(long arrivalId)
    {
        using var c = Db();
        await c.OpenAsync();
        await c.ExecuteAsync(@"
            DECLARE @qos TABLE (id BIGINT);
            INSERT INTO @qos SELECT quality_order_id FROM qms_quality_order WHERE arrival_id = @arrivalId;

            DELETE FROM qms_status_history
             WHERE entity_type = 'Survey'
               AND entity_id IN (SELECT survey_id FROM qms_survey WHERE arrival_id = @arrivalId);
            DELETE FROM qms_survey WHERE arrival_id = @arrivalId;
            DELETE FROM qms_claim_note WHERE claim_id IN
                (SELECT claim_id FROM qms_claim WHERE quality_order_id IN (SELECT id FROM @qos));
            DELETE FROM qms_claim WHERE quality_order_id IN (SELECT id FROM @qos);
            DELETE FROM qms_status_history
             WHERE (entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos))
                OR (entity_type = 'Arrival' AND entity_id = @arrivalId);
            DELETE FROM qms_quality_order_material WHERE quality_order_id IN (SELECT id FROM @qos);
            DELETE FROM qms_quality_order WHERE arrival_id = @arrivalId;
            DELETE FROM qms_shipment_snapshot WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival_item WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival WHERE arrival_id = @arrivalId;", new { arrivalId });
    }

    private async Task<T> WithSurveysAsync<T>(Func<ISurveyService, Task<T>> work)
    {
        using var s = _factory.Services.CreateScope();
        return await work(s.ServiceProvider.GetRequiredService<ISurveyService>());
    }

    private Task<SurveyRequestResult> RequestAsync(long arrivalId, long? qoId = null, string reason = "Mould on several pallets") =>
        WithSurveysAsync(svc => svc.RequestAsync(arrivalId,
            qoId.HasValue ? SurveySource.QualityOrder : SurveySource.Arrival, qoId, reason, "test comments", "test"));

    private async Task<bool?> JointSurveyAsync(long arrivalId)
    {
        using var c = Db();
        return await c.ExecuteScalarAsync<bool?>(
            "SELECT joint_survey FROM qms_shipment_snapshot WHERE arrival_id = @arrivalId", new { arrivalId });
    }

    private async Task<string?> StatusAsync(long surveyId)
    {
        using var c = Db();
        return await c.ExecuteScalarAsync<string?>(
            "SELECT status_code FROM qms_survey WHERE survey_id = @surveyId", new { surveyId });
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token on " + url);
        return m.Groups[1].Value;
    }

    // ---- request ----------------------------------------------------

    [Fact]
    public async Task Asking_from_the_arrival_creates_an_open_survey_and_answers_joint_survey()
    {
        // A draft arrival with no quality order: the usual moment to ask.
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            Assert.Null(await JointSurveyAsync(arrivalId));
            var res = await RequestAsync(arrivalId);

            Assert.True(res.Ok, res.Error);
            Assert.False(res.AlreadyExisted);
            Assert.Matches(@"^SRV-\d{4}-\d{6}$", res.SurveyNo!);
            Assert.Equal(SurveyStatus.Open, res.StatusCode);
            Assert.True(await JointSurveyAsync(arrivalId));

            var s = await WithSurveysAsync(svc => svc.GetAsync(res.SurveyId!.Value));
            Assert.NotNull(s);
            Assert.Equal(SurveySource.Arrival, s!.SourceCode);
            Assert.Equal(SurveyMethod.Manual, s.MethodCode);
            Assert.Null(s.SourceQoId);
            Assert.Null(s.QualityOrderId);              // none exists yet, and none was needed
            Assert.Equal("test", s.RequestedBy);
            Assert.Equal("Mould on several pallets", s.RequestReason);
            Assert.Equal(1, s.ProductCount);

            using var c = Db();
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_status_history WHERE entity_type = 'Survey' AND entity_id = @id AND new_status = 'Open'",
                new { id = res.SurveyId }));
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_audit_log WHERE entity_type = 'Survey' AND entity_id = @id AND action_code = 'Created'",
                new { id = res.SurveyId }));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task Asking_from_the_quality_order_links_the_order_and_its_arrival()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        try
        {
            var res = await RequestAsync(arrivalId, qoId);
            Assert.True(res.Ok, res.Error);

            var s = await WithSurveysAsync(svc => svc.GetAsync(res.SurveyId!.Value));
            Assert.Equal(SurveySource.QualityOrder, s!.SourceCode);
            Assert.Equal(qoId, s.SourceQoId);
            Assert.Equal(qoId, s.QualityOrderId);
            Assert.Equal(arrivalId, s.ArrivalId);
            // Asking from the order answers the arrival's Joint Survey too.
            Assert.True(await JointSurveyAsync(arrivalId));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task An_order_created_after_the_survey_is_picked_up_without_a_second_survey()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Completed, null);
        try
        {
            var res = await RequestAsync(arrivalId);
            Assert.True(res.Ok, res.Error);

            long qoId;
            using (var scope = _factory.Services.CreateScope())
                qoId = await scope.ServiceProvider.GetRequiredService<IQualityOrderService>()
                    .CreateForArrivalAsync(arrivalId, "test");

            var s = await WithSurveysAsync(svc => svc.GetAsync(res.SurveyId!.Value));
            Assert.Equal(qoId, s!.QualityOrderId);
            Assert.Equal(SurveySource.Arrival, s.SourceCode);      // where it was asked stays as it was

            using var c = Db();
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_survey WHERE arrival_id = @arrivalId", new { arrivalId }));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_second_request_returns_the_first_survey_whatever_its_status()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        try
        {
            var first = await RequestAsync(arrivalId);
            var fromQo = await RequestAsync(arrivalId, qoId);
            Assert.True(fromQo.Ok);
            Assert.True(fromQo.AlreadyExisted);
            Assert.Equal(first.SurveyNo, fromQo.SurveyNo);

            // Passed is no different: the arrival has had its one survey.
            var (ok, error) = await WithSurveysAsync(svc => svc.DecideAsync(first.SurveyId!.Value, false, "Not needed after all", "test"));
            Assert.True(ok, error);
            var afterPass = await RequestAsync(arrivalId);
            Assert.True(afterPass.AlreadyExisted);
            Assert.Equal(SurveyStatus.Pass, afterPass.StatusCode);

            using var c = Db();
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_survey WHERE arrival_id = @arrivalId", new { arrivalId }));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task Requests_at_the_same_moment_create_exactly_one_survey()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        try
        {
            // Half from the arrival, half from the order, all at once.
            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(i => Task.Run(() => RequestAsync(arrivalId, i % 2 == 0 ? null : qoId))));

            Assert.All(results, r => Assert.True(r.Ok, r.Error));
            Assert.Single(results, r => !r.AlreadyExisted);
            Assert.Single(results.Select(r => r.SurveyNo).Distinct());

            using var c = Db();
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_survey WHERE arrival_id = @arrivalId", new { arrivalId }));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_request_needs_a_reason_and_a_real_arrival()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        var (otherArrival, otherQo, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        try
        {
            Assert.False((await RequestAsync(arrivalId, reason: "no")).Ok);
            Assert.False((await RequestAsync(arrivalId, reason: new string('x', 501))).Ok);
            Assert.False((await RequestAsync(-1)).Ok);
            // An order that belongs to a different arrival is refused.
            Assert.False((await RequestAsync(arrivalId, otherQo)).Ok);

            using var c = Db();
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_survey WHERE arrival_id IN (@arrivalId, @otherArrival)", new { arrivalId, otherArrival }));
            Assert.Null(await JointSurveyAsync(arrivalId));
        }
        finally { await CleanupAsync(arrivalId); await CleanupAsync(otherArrival); }
    }

    // ---- decision ---------------------------------------------------

    [Fact]
    public async Task Confirm_and_pass_only_work_once_and_only_from_open()
    {
        var (a1, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        var (a2, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var s1 = (await RequestAsync(a1)).SurveyId!.Value;
            var s2 = (await RequestAsync(a2)).SurveyId!.Value;

            // Passing needs a reason; refusing leaves the survey Open.
            var noReason = await WithSurveysAsync(svc => svc.DecideAsync(s1, false, "  ", "decider"));
            Assert.False(noReason.ok);
            Assert.Equal(SurveyStatus.Open, await StatusAsync(s1));

            var confirm = await WithSurveysAsync(svc => svc.DecideAsync(s1, true, null, "decider"));
            Assert.True(confirm.ok, confirm.error);
            var confirmed = await WithSurveysAsync(svc => svc.GetAsync(s1));
            Assert.Equal(SurveyStatus.Confirm, confirmed!.StatusCode);
            Assert.Equal("decider", confirmed.DecidedBy);
            Assert.NotNull(confirmed.DecidedAt);

            // No way back, and no way sideways.
            var again = await WithSurveysAsync(svc => svc.DecideAsync(s1, false, "Changed my mind", "other"));
            Assert.False(again.ok);
            Assert.Contains("already confirmed by decider", again.error);
            Assert.Equal(SurveyStatus.Confirm, await StatusAsync(s1));

            var pass = await WithSurveysAsync(svc => svc.DecideAsync(s2, false, "Only one damaged carton", "decider"));
            Assert.True(pass.ok, pass.error);
            var passed = await WithSurveysAsync(svc => svc.GetAsync(s2));
            Assert.Equal(SurveyStatus.Pass, passed!.StatusCode);
            Assert.Equal("Only one damaged carton", passed.DecisionComments);
            Assert.False((await WithSurveysAsync(svc => svc.DecideAsync(s2, true, null, "other"))).ok);

            Assert.False((await WithSurveysAsync(svc => svc.DecideAsync(-5, true, null, "other"))).ok);
        }
        finally { await CleanupAsync(a1); await CleanupAsync(a2); }
    }

    [Fact]
    public async Task Two_decisions_at_the_same_moment_leave_one()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var id = (await RequestAsync(arrivalId)).SurveyId!.Value;
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() =>
                WithSurveysAsync(svc => svc.DecideAsync(id, i % 2 == 0, "Decided at the same time", "user" + i)))));

            Assert.Single(results, r => r.ok);
            using var c = Db();
            Assert.Equal(1, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_status_history WHERE entity_type = 'Survey' AND entity_id = @id AND old_status = 'Open'",
                new { id }));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- who may do what --------------------------------------------

    [Fact]
    public async Task Seeing_asking_and_deciding_are_three_separate_rights()
    {
        var perms = _factory.Services.GetRequiredService<IPermissionResolver>();
        await perms.EnsureLoadedAsync();
        bool Can(string role, string code, bool screen = false) =>
            perms.RoleHas(role, code, screen ? AccessLevel.Read : AccessLevel.Edit, isScreen: screen);

        // The page and the decision: managers, the claim manager, the administrator.
        foreach (var r in new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.ClaimManager })
        {
            Assert.True(Can(r, Screens.Surveys, screen: true), r + " should open Surveys");
            Assert.True(Can(r, Perm.Surveys.Decide), r + " should decide");
        }
        foreach (var r in new[] { RoleCodes.Supervisor, RoleCodes.Operator, RoleCodes.Viewer })
        {
            Assert.False(Can(r, Screens.Surveys, screen: true), r + " should not open Surveys");
            Assert.False(Can(r, Perm.Surveys.Decide), r + " should not decide");
        }
        // Asking is the inspectors' right -- and they hold it WITHOUT the page.
        foreach (var r in new[] { RoleCodes.Operator, RoleCodes.Supervisor, RoleCodes.Manager, RoleCodes.Admin })
            Assert.True(Can(r, Perm.Surveys.Request), r + " should request");
        Assert.False(Can(RoleCodes.Viewer, Perm.Surveys.Request));
    }

    [Fact]
    public async Task An_operator_can_ask_but_cannot_open_or_decide_even_by_posting_directly()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var op = Client(RoleCodes.Operator);
            var arrivalUrl = "/Arrivals/Details/" + arrivalId;

            // The dialog is on the arrival page for them.
            var page = await op.GetStringAsync(arrivalUrl);
            Assert.Contains("id=\"surveyRequestModal\"", page);

            var ask = await op.PostAsync("/Surveys/RequestSurvey", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(op, arrivalUrl)),
                new KeyValuePair<string, string>("arrivalId", arrivalId.ToString()),
                new KeyValuePair<string, string>("reason", "Cartons wet and collapsed"),
            }));
            Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
            var json = await ask.Content.ReadAsStringAsync();
            Assert.Contains("\"ok\":true", json);
            Assert.Contains("\"url\":null", json);          // not handed the address of a page they cannot open

            var survey = await WithSurveysAsync(svc => svc.GetBriefForArrivalAsync(arrivalId));
            Assert.NotNull(survey);

            // Their arrival page now shows the survey, locked, with no link.
            page = await op.GetStringAsync(arrivalUrl);
            Assert.Contains(survey!.SurveyNo, page);
            Assert.DoesNotContain("id=\"surveyRequestModal\"", page);
            Assert.DoesNotContain("/Surveys/Details/", page);

            Assert.NotEqual(HttpStatusCode.OK, (await op.GetAsync("/Surveys")).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await op.GetAsync("/Surveys/Details/" + survey.SurveyId)).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await op.GetAsync("/Surveys/Export")).StatusCode);

            foreach (var action in new[] { "Confirm", "Pass" })
            {
                await op.PostAsync($"/Surveys/{action}/{survey.SurveyId}", new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(op, arrivalUrl)),
                    new KeyValuePair<string, string>("comments", "An operator trying to decide"),
                }));
                Assert.Equal(SurveyStatus.Open, await StatusAsync(survey.SurveyId));
            }
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_viewer_cannot_ask()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var viewer = Client(RoleCodes.Viewer);
            var arrivalUrl = "/Arrivals/Details/" + arrivalId;
            var page = await viewer.GetStringAsync(arrivalUrl);
            Assert.DoesNotContain("id=\"surveyRequestModal\"", page);

            var res = await viewer.PostAsync("/Surveys/RequestSurvey", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(viewer, arrivalUrl)),
                new KeyValuePair<string, string>("arrivalId", arrivalId.ToString()),
                new KeyValuePair<string, string>("reason", "A viewer trying to ask"),
            }));
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.Null(await WithSurveysAsync(svc => svc.GetBriefForArrivalAsync(arrivalId)));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- pages ------------------------------------------------------

    [Fact]
    public async Task The_banner_shows_for_open_and_confirmed_and_never_for_passed()
    {
        var (aOpen, qOpen, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        var (aConf, qConf, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Closed);
        var (aPass, qPass, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        var (aNone, qNone, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        try
        {
            await RequestAsync(aOpen, qOpen);
            var conf = (await RequestAsync(aConf, qConf)).SurveyId!.Value;
            var pass = (await RequestAsync(aPass, qPass)).SurveyId!.Value;
            Assert.True((await WithSurveysAsync(svc => svc.DecideAsync(conf, true, null, "test"))).ok);
            Assert.True((await WithSurveysAsync(svc => svc.DecideAsync(pass, false, "Not required", "test"))).ok);

            var admin = Client(RoleCodes.Admin);
            async Task<string> Qo(long? id) => await admin.GetStringAsync("/QualityOrders/Details/" + id);

            Assert.Contains("UNDER SURVEY", await Qo(qOpen));
            // Confirmed keeps the banner even though the order is finished.
            Assert.Contains("UNDER SURVEY", await Qo(qConf));
            Assert.DoesNotContain("UNDER SURVEY", await Qo(qPass));
            var none = await Qo(qNone);
            Assert.DoesNotContain("UNDER SURVEY", none);
            // Only the order with no survey still offers the request.
            Assert.Contains("id=\"surveyRequestModal\"", none);
            Assert.DoesNotContain("id=\"surveyRequestModal\"", await Qo(qPass));

            // The claim view of the same order carries the banner too.
            Assert.Contains("UNDER SURVEY", await admin.GetStringAsync("/ClaimManagement/Details/" + qConf));
        }
        finally
        {
            foreach (var a in new[] { aOpen, aConf, aPass, aNone }) await CleanupAsync(a);
        }
    }

    [Fact]
    public async Task The_survey_tab_lists_filters_and_exports()
    {
        var (arrivalId, qoId, tag) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Open);
        try
        {
            var res = await RequestAsync(arrivalId, qoId);
            var admin = Client(RoleCodes.Admin);

            var list = await admin.GetStringAsync("/Surveys?search=" + tag);
            Assert.Contains(res.SurveyNo!, list);
            Assert.Contains("QO-TEST-" + tag, list);
            // The tab strip is shared with Claims.
            Assert.Contains("/ClaimManagement/Archived", list);
            Assert.Contains("href=\"/Surveys\"", await admin.GetStringAsync("/ClaimManagement"));

            Assert.DoesNotContain(res.SurveyNo!, await admin.GetStringAsync($"/Surveys?search={tag}&status=Pass"));
            Assert.DoesNotContain(res.SurveyNo!, await admin.GetStringAsync($"/Surveys?search={tag}&source=Arrival"));
            Assert.Contains(res.SurveyNo!, await admin.GetStringAsync($"/Surveys?search={tag}&status=Open&source=QualityOrder&plant=JD01"));

            var details = await admin.GetStringAsync("/Surveys/Details/" + res.SurveyId);
            Assert.Contains("Mould on several pallets", details);
            Assert.Contains("id=\"confirmSurveyModal\"", details);
            Assert.Contains("Survey requested", details);                 // history

            var export = await admin.GetAsync("/Surveys/Export?search=" + tag);
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            var bytes = await export.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K', "The export is not an .xlsx file.");

            // The three statuses account for every survey.
            var counts = await WithSurveysAsync(svc => svc.CountsAsync(PlantScope.All));
            Assert.True(counts.Open >= 1);
            Assert.Equal(counts.Total, counts.Open + counts.Confirmed + counts.Passed);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_branch_restricted_user_sees_only_their_branches()
    {
        var (arrivalId, _, tag) = await SeedAsync(ArrivalStatus.Draft, null);     // plant JD01
        try
        {
            await RequestAsync(arrivalId);
            var other = new PlantScope(false, new[] { "ZZ99" });
            var mine  = new PlantScope(false, new[] { "JD01" });
            var f = new SurveyListFilter { Search = tag };

            Assert.Empty((await WithSurveysAsync(svc => svc.ListAsync(f, other))).Rows);
            Assert.Single((await WithSurveysAsync(svc => svc.ListAsync(f, mine))).Rows);
            Assert.Equal(0, (await WithSurveysAsync(svc => svc.CountsAsync(other))).Total);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- Joint Survey on the arrival form ---------------------------

    [Fact]
    public async Task The_shipment_form_cannot_set_joint_survey_to_yes_or_take_a_survey_back()
    {
        var (aNo, _, _)  = await SeedAsync(ArrivalStatus.Draft, null);
        var (aYes, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var admin = Client(RoleCodes.Admin);
            async Task SaveAsync(long arrivalId, string jointSurvey) =>
                await admin.PostAsync("/Arrivals/SaveShipment", new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, "/Arrivals/Details/" + arrivalId)),
                    new KeyValuePair<string, string>("ArrivalId", arrivalId.ToString()),
                    new KeyValuePair<string, string>("JointSurvey", jointSurvey),
                }));

            // Yes posted straight at the form: no survey, so no Yes.
            await SaveAsync(aNo, "true");
            Assert.NotEqual(true, await JointSurveyAsync(aNo));
            Assert.Null(await WithSurveysAsync(svc => svc.GetBriefForArrivalAsync(aNo)));
            // No is still an ordinary answer.
            await SaveAsync(aNo, "false");
            Assert.False(await JointSurveyAsync(aNo));

            // Once a survey exists the answer stays Yes whatever the form posts.
            await RequestAsync(aYes);
            await SaveAsync(aYes, "false");
            Assert.True(await JointSurveyAsync(aYes));
        }
        finally { await CleanupAsync(aNo); await CleanupAsync(aYes); }
    }

    [Fact]
    public async Task Deleting_the_arrival_takes_its_survey_with_it()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        var deleted = false;
        try
        {
            var res = await RequestAsync(arrivalId);
            using (var scope = _factory.Services.CreateScope())
            {
                var (ok, error) = await scope.ServiceProvider.GetRequiredService<IArrivalService>()
                    .DeleteAsync(arrivalId, "test");
                Assert.True(ok, error);
                deleted = true;
            }
            Assert.Null(await StatusAsync(res.SurveyId!.Value));
        }
        finally { if (!deleted) await CleanupAsync(arrivalId); }
    }

    // ---- nothing reaches the supplier -------------------------------

    private static DirectoryInfo WebProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "SharbatlyQMS.Web");
            if (Directory.Exists(candidate)) return new DirectoryInfo(candidate);
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate SharbatlyQMS.Web above " + AppContext.BaseDirectory);
    }

    private static string WithoutComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(text, "//[^\n]*", "");
    }

    /// <summary>
    /// The QC report is the one thing a supplier receives. Its data object
    /// carries no survey field, and neither renderer mentions a survey -- not
    /// the new record, and not the old Joint Survey line, which is now how a
    /// survey is requested.
    /// </summary>
    [Fact]
    public void The_qc_report_has_no_way_to_print_anything_about_a_survey()
    {
        Assert.DoesNotContain(typeof(QualityReportData).GetProperties(),
            p => p.Name.Contains("Survey", StringComparison.OrdinalIgnoreCase));

        var pdf = Path.Combine(WebProjectDir().FullName, "Services", "Pdf");
        foreach (var file in new[] { "QualityReportPdf.cs", "QualityReportPdfSoft.cs", "QualityReportData.cs", "ReportLabels.cs" })
        {
            var code = WithoutComments(File.ReadAllText(Path.Combine(pdf, file)));
            Assert.False(code.Contains("survey", StringComparison.OrdinalIgnoreCase),
                file + " mentions a survey outside a comment. Nothing about surveys may be printed for the supplier.");
        }
    }
}
