using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Survey management, stage 3: the committee and what it does once a survey is
/// confirmed -- assignment, the appointment, attendance, the QC officer's
/// inspection confirmation, and attachments.
///
/// Two rules are held throughout: every committee activity needs a CONFIRMED
/// survey, and none of it changes the survey's status.
///
/// The tests use branch codes no real branch has, set up their own committee
/// for them, and remove it again; the suite runs against the live database.
/// Notices go to stand-in recipients (see SurveyTestDoubles).
/// </summary>
[Collection("workflow")]
public class SurveyCommitteeTests : IClassFixture<QmsAppFactory>, IAsyncLifetime
{
    private const string Plant     = SurveyTestData.TestPlant;
    private const string BarePlant = SurveyTestData.TestPlantBare;
    private const int    InsuranceUser = -4410;      // even: "can open" the Surveys page
    private const int    ClaimsUser    = -4411;      // odd: cannot
    private const string ExternalMail  = "survey-test-external@example.invalid";
    private const string SurveyorMail  = "survey-test-surveyor@example.invalid";

    private readonly QmsAppFactory _factory;
    public SurveyCommitteeTests(QmsAppFactory factory) => _factory = factory;

    public Task InitializeAsync() => SurveyTestData.CleanupCommitteesAsync(_factory);
    public Task DisposeAsync()    => SurveyTestData.CleanupCommitteesAsync(_factory);

    // ---- plumbing ---------------------------------------------------

    private Task CleanupAsync(long arrivalId) => SurveyTestData.CleanupAsync(_factory, arrivalId);

    private async Task<T> With<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        using var s = _factory.Services.CreateScope();
        return await work(s.ServiceProvider.GetRequiredService<TService>());
    }

    private Task<T> Committee<T>(Func<ISurveyCommitteeService, Task<T>> work) => With(work);

    /// <summary>A committee for <paramref name="plant"/>: two internal members,
    /// one external, and one inactive member who must never be copied.</summary>
    private async Task<int> SeedCommitteeAsync(string plant, string name = "Test committee")
    {
        using var c = SurveyTestData.Db(_factory);
        var id = await c.ExecuteScalarAsync<int>(@"
            INSERT INTO qms_survey_committee (committee_name, plant, is_active, created_by, updated_by)
            VALUES (@name, @plant, 1, 'test', 'test');
            SELECT CAST(SCOPE_IDENTITY() AS INT);", new { name, plant });
        await c.ExecuteAsync(@"
            INSERT INTO qms_survey_committee_member
                (committee_id, participant_type, user_id, member_name, organisation, committee_role, email,
                 notify_in_app, notify_email, expected_attendance, is_active, sort_order)
            VALUES (@id, 'Internal', @ins, 'Insurance Person', NULL, 'Insurance', NULL, 1, 1, 1, 1, 10),
                   (@id, 'Internal', @clm, 'Claims Person',    NULL, 'Claims',    NULL, 1, 0, 1, 1, 20),
                   (@id, 'External', NULL, 'Outside Surveyor', 'Survey Co', 'Surveyor', @ext, 0, 1, 1, 1, 30),
                   (@id, 'External', NULL, 'Former Member',    'Old Co', 'Other', 'old@example.invalid', 0, 1, 1, 0, 40)",
            new { id, ins = InsuranceUser, clm = ClaimsUser, ext = ExternalMail });
        return id;
    }

    /// <summary>A survey on the test branch, confirmed unless told otherwise.</summary>
    private async Task<(long ArrivalId, long? QoId, long SurveyId, string SurveyNo)> SurveyAsync(
        bool confirm = true, string plant = Plant, string? qoStatus = QualityOrderStatus.Open)
    {
        var (arrivalId, qoId, _) = await SurveyTestData.SeedAsync(_factory, ArrivalStatus.Completed, qoStatus, plant: plant);
        var res = await With<ISurveyService, SurveyRequestResult>(svc => svc.RequestAsync(arrivalId,
            SurveySource.Arrival, null, "Mould on several pallets", null, TestSurveyRecipients.RequesterName));
        Assert.True(res.Ok, res.Error);
        if (confirm)
        {
            var (ok, error) = await With<ISurveyService, (bool ok, string? error)>(svc =>
                svc.DecideAsync(res.SurveyId!.Value, true, "Formal survey needed", "decider"));
            Assert.True(ok, error);
        }
        return (arrivalId, qoId, res.SurveyId!.Value, res.SurveyNo!);
    }

    private static AppointmentInput Appointment(DateTime day, string time = "10:30") => new()
    {
        PlannedDate = day, PlannedTime = time, Location = "Cold store 3",
        SurveyCompany = "Survey Co", SurveyorName = "Outside Surveyor", SurveyorEmail = SurveyorMail,
        Instructions = "Bring the seal cutter", InternalComments = "INTERNAL-ONLY-NOTE"
    };

    private Task<Survey?> GetAsync(long surveyId) => With<ISurveyService, Survey?>(svc => svc.GetAsync(surveyId));

    private sealed class Notice
    {
        public int     UserId    { get; set; }
        public string  EventCode { get; set; } = "";
        public string? LinkUrl   { get; set; }
    }

    private async Task<List<Notice>> BellAsync(long surveyId, string eventCode)
    {
        using var c = SurveyTestData.Db(_factory);
        return (await c.QueryAsync<Notice>(@"
            SELECT user_id AS UserId, event_code AS EventCode, link_url AS LinkUrl
            FROM   qms_notification
            WHERE  entity_type = 'Survey' AND entity_id = @surveyId AND event_code = @eventCode",
            new { surveyId, eventCode })).ToList();
    }

    private sealed class Mail
    {
        public string ToAddress { get; set; } = "";
        public string Subject   { get; set; } = "";
        public string BodyHtml  { get; set; } = "";
    }

    private async Task<List<Mail>> MailsAsync(long surveyId, string eventCode)
    {
        using var c = SurveyTestData.Db(_factory);
        return (await c.QueryAsync<Mail>(@"
            SELECT to_address AS ToAddress, subject AS Subject, body_html AS BodyHtml
            FROM   qms_mail_outbox
            WHERE  entity_type = 'Survey' AND entity_id = @surveyId AND event_code = @eventCode",
            new { surveyId, eventCode })).ToList();
    }

    private HttpClient Client(string role)
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        return c;
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token on " + url);
        return m.Groups[1].Value;
    }

    // ---- configuration ----------------------------------------------

    [Fact]
    public async Task One_active_committee_per_branch_and_members_are_validated()
    {
        var save = await Committee(svc => svc.SaveCommitteeAsync(
            new SurveyCommittee { CommitteeName = "Branch committee", Plant = Plant, IsActive = true }, "test"));
        Assert.True(save.ok, save.error);
        var k = (await Committee(svc => svc.ListCommitteesAsync())).Single(x => x.Plant == Plant);

        // A second ACTIVE committee for the same branch is refused; an inactive one is fine.
        var second = await Committee(svc => svc.SaveCommitteeAsync(
            new SurveyCommittee { CommitteeName = "Another", Plant = Plant, IsActive = true }, "test"));
        Assert.False(second.ok);
        Assert.Contains("already has an active committee", second.error);
        Assert.True((await Committee(svc => svc.SaveCommitteeAsync(
            new SurveyCommittee { CommitteeName = "Spare", Plant = Plant, IsActive = false }, "test"))).ok);
        Assert.False((await Committee(svc => svc.SaveCommitteeAsync(
            new SurveyCommittee { CommitteeName = "  ", Plant = Plant }, "test"))).ok);

        // An internal member must be a real, active QMS user; its name comes from the account.
        var noUser = await Committee(svc => svc.SaveMemberAsync(new SurveyCommitteeMember
        { CommitteeId = k.CommitteeId, ParticipantType = ParticipantType.Internal, UserId = -1, CommitteeRole = CommitteeRole.QC }, "test"));
        Assert.False(noUser.ok);
        var real = await Committee(svc => svc.SaveMemberAsync(new SurveyCommitteeMember
        {
            CommitteeId = k.CommitteeId, ParticipantType = ParticipantType.Internal, UserId = QmsAppFactory.AdminUserId,
            MemberName = "typed name is ignored", CommitteeRole = CommitteeRole.QC
        }, "test"));
        Assert.True(real.ok, real.error);

        // An external member needs a name and a plausible address, and never gets a bell.
        Assert.False((await Committee(svc => svc.SaveMemberAsync(new SurveyCommitteeMember
        { CommitteeId = k.CommitteeId, ParticipantType = ParticipantType.External, CommitteeRole = CommitteeRole.Surveyor }, "test"))).ok);
        Assert.False((await Committee(svc => svc.SaveMemberAsync(new SurveyCommitteeMember
        {
            CommitteeId = k.CommitteeId, ParticipantType = ParticipantType.External, MemberName = "Surveyor",
            Email = "not-an-address", CommitteeRole = CommitteeRole.Surveyor
        }, "test"))).ok);
        Assert.True((await Committee(svc => svc.SaveMemberAsync(new SurveyCommitteeMember
        {
            CommitteeId = k.CommitteeId, ParticipantType = ParticipantType.External, MemberName = "Surveyor",
            Organisation = "Survey Co", Email = ExternalMail, CommitteeRole = CommitteeRole.Surveyor, NotifyInApp = true
        }, "test"))).ok);
        Assert.False((await Committee(svc => svc.SaveMemberAsync(new SurveyCommitteeMember
        { CommitteeId = k.CommitteeId, ParticipantType = ParticipantType.External, MemberName = "X", CommitteeRole = "Boss" }, "test"))).ok);

        var members = (await Committee(svc => svc.ListCommitteesAsync())).Single(x => x.CommitteeId == k.CommitteeId).Members;
        Assert.Equal(2, members.Count);
        var internalMember = members.Single(m => m.IsInternal);
        Assert.Equal(QmsAppFactory.AdminUserId, internalMember.UserId);
        Assert.NotEqual("typed name is ignored", internalMember.MemberName);
        Assert.False(members.Single(m => !m.IsInternal).NotifyInApp);

        Assert.True((await Committee(svc => svc.DeleteMemberAsync(internalMember.MemberId, "test"))).ok);
        Assert.True((await Committee(svc => svc.DeleteCommitteeAsync(k.CommitteeId, "test"))).ok);
        Assert.DoesNotContain(await Committee(svc => svc.ListCommitteesAsync()), x => x.CommitteeId == k.CommitteeId);
    }

    [Fact]
    public async Task A_new_committee_leads_straight_into_adding_its_members()
    {
        var admin = Client(RoleCodes.Admin);
        async Task<HttpResponseMessage> PostAsync(params (string k, string v)[] fields) =>
            await admin.PostAsync("/SurveyCommittees/SaveCommittee", new FormUrlEncodedContent(
                fields.Select(f => new KeyValuePair<string, string>(f.k, f.v)).Append(
                    new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, "/SurveyCommittees")))));

        var created = await PostAsync(("CommitteeId", "0"), ("CommitteeName", "Posted committee"), ("Plant", Plant), ("IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var k = (await Committee(svc => svc.ListCommitteesAsync())).Single(x => x.Plant == Plant);
        Assert.EndsWith("/SurveyCommittees?addMemberTo=" + k.CommitteeId, created.Headers.Location!.OriginalString);

        // The page carries the button the script opens, the picker of QMS users
        // and the fields for an outside person: a member can be either.
        var html = await admin.GetStringAsync(created.Headers.Location!.OriginalString);
        Assert.Contains("data-add-member=\"" + k.CommitteeId + "\"", html);
        Assert.Contains("name=\"UserId\"", html);
        Assert.Contains("name=\"MemberName\"", html);
        Assert.Contains("value=\"" + QmsAppFactory.AdminUserId + "\"", html);

        // Editing an existing committee just comes back to the list.
        var edited = await PostAsync(("CommitteeId", k.CommitteeId.ToString()), ("CommitteeName", "Renamed"), ("Plant", Plant), ("IsActive", "true"));
        Assert.EndsWith("/SurveyCommittees", edited.Headers.Location!.OriginalString);
        Assert.Equal("Renamed", (await Committee(svc => svc.ListCommitteesAsync())).Single(x => x.CommitteeId == k.CommitteeId).CommitteeName);
    }

    [Fact]
    public async Task A_new_committee_is_saved_with_the_users_picked_and_the_outside_people_typed()
    {
        var admin = Client(RoleCodes.Admin);

        // The dialog itself offers both kinds of member, and the user list
        // puts the people who can open Surveys first.
        var page = await admin.GetStringAsync("/SurveyCommittees");
        foreach (var expected in new[] { "id=\"kRowInternal\"", "id=\"kRowExternal\"", "Add a QMS user",
                     "Add an outside person", "Users with access to Surveys", "Committee name" })
            Assert.Contains(expected, page);

        var users = await Committee(svc => svc.ListUsersAsync());
        Assert.True(users.Single(u => u.UserId == QmsAppFactory.AdminUserId).CanOpenSurveys);
        // Everybody active is offered; the ones with access come before the rest.
        var firstWithout = users.ToList().FindIndex(u => !u.CanOpenSurveys);
        if (firstWithout >= 0) Assert.DoesNotContain(users.Skip(firstWithout), u => u.CanOpenSurveys);

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await TokenAsync(admin, "/SurveyCommittees")),
            new("CommitteeId", "0"), new("CommitteeName", "Picked committee"), new("Plant", Plant), new("IsActive", "true"),
            // a QMS user
            new("Members[0].ParticipantType", ParticipantType.Internal),
            new("Members[0].UserId", QmsAppFactory.AdminUserId.ToString()),
            new("Members[0].CommitteeRole", CommitteeRole.QC),
            // a row left empty
            new("Members[1].ParticipantType", ParticipantType.Internal),
            new("Members[1].UserId", ""),
            new("Members[1].CommitteeRole", CommitteeRole.Other),
            // the same user again
            new("Members[2].ParticipantType", ParticipantType.Internal),
            new("Members[2].UserId", QmsAppFactory.AdminUserId.ToString()),
            new("Members[2].CommitteeRole", CommitteeRole.Claims),
            // an outside person
            new("Members[3].ParticipantType", ParticipantType.External),
            new("Members[3].MemberName", "Outside Surveyor"),
            new("Members[3].Organisation", "Survey Co"),
            new("Members[3].Email", ExternalMail),
            new("Members[3].CommitteeRole", CommitteeRole.Surveyor),
            // an outside person whose address is wrong
            new("Members[4].ParticipantType", ParticipantType.External),
            new("Members[4].MemberName", "Bad Address"),
            new("Members[4].Email", "not-an-address"),
            new("Members[4].CommitteeRole", CommitteeRole.Insurance),
        };
        var created = await admin.PostAsync("/SurveyCommittees/SaveCommittee", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        // Members came with it, so there is no "now add the members" step.
        Assert.EndsWith("/SurveyCommittees", created.Headers.Location!.OriginalString);

        var k = (await Committee(svc => svc.ListCommitteesAsync())).Single(x => x.Plant == Plant);
        Assert.Equal("Picked committee", k.CommitteeName);
        Assert.Equal(2, k.Members.Count);

        var user = k.Members.Single(m => m.IsInternal);
        Assert.Equal(QmsAppFactory.AdminUserId, user.UserId);
        Assert.Equal(CommitteeRole.QC, user.CommitteeRole);          // the first pick, not the repeat
        Assert.False(string.IsNullOrWhiteSpace(user.MemberName));    // from the account
        Assert.True(user.NotifyInApp);
        Assert.True(user.ExpectedAttendance);

        var outside = k.Members.Single(m => !m.IsInternal);
        Assert.Equal("Outside Surveyor", outside.MemberName);
        Assert.Equal("Survey Co", outside.Organisation);
        Assert.Equal(ExternalMail, outside.Email);
        Assert.Equal(CommitteeRole.Surveyor, outside.CommitteeRole);
        Assert.False(outside.NotifyInApp);                           // no account, no bell
        Assert.True(user.SortOrder < outside.SortOrder);

        // The refused row is named, not silently dropped.
        var after = await admin.GetStringAsync("/SurveyCommittees");
        Assert.Contains("Bad Address", after);
        Assert.Contains("could not be added", after);
    }

    [Fact]
    public async Task Only_an_administrator_can_change_the_committee_setup()
    {
        var perms = _factory.Services.GetRequiredService<IPermissionResolver>();
        await perms.EnsureLoadedAsync();
        Assert.True(perms.RoleHas(RoleCodes.Admin, Perm.Parameters.SurveyCommitteesEdit, AccessLevel.Edit, isScreen: false));
        foreach (var r in new[] { RoleCodes.Manager, RoleCodes.ClaimManager, RoleCodes.Supervisor, RoleCodes.Operator })
            Assert.False(perms.RoleHas(r, Perm.Parameters.SurveyCommitteesEdit, AccessLevel.Edit, isScreen: false));

        Assert.NotEqual(HttpStatusCode.OK, (await Client(RoleCodes.Operator).GetAsync("/SurveyCommittees")).StatusCode);
        var admin = Client(RoleCodes.Admin);
        Assert.Contains("Survey committees", await admin.GetStringAsync("/SurveyCommittees"));

        // A manager may look, but a direct post changes nothing.
        var manager = Client(RoleCodes.Manager);
        var page = await manager.GetAsync("/SurveyCommittees");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("id=\"committeeModal\"", await page.Content.ReadAsStringAsync());
        await manager.PostAsync("/SurveyCommittees/SaveCommittee", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, "/SurveyCommittees")),
            new KeyValuePair<string, string>("CommitteeName", "Posted by a manager"),
            new KeyValuePair<string, string>("Plant", Plant),
            new KeyValuePair<string, string>("IsActive", "true"),
        }));
        Assert.DoesNotContain(await Committee(svc => svc.ListCommitteesAsync()), x => x.CommitteeName == "Posted by a manager");
    }

    // ---- assignment -------------------------------------------------

    [Fact]
    public async Task Confirming_copies_the_branch_committee_and_later_changes_do_not_rewrite_it()
    {
        var committeeId = await SeedCommitteeAsync(Plant);
        var s = await SurveyAsync();
        try
        {
            var survey = await GetAsync(s.SurveyId);
            Assert.Equal(committeeId, survey!.CommitteeId);
            Assert.Equal("Test committee", survey.CommitteeName);
            Assert.NotNull(survey.CommitteeAssignedAt);

            var members = await Committee(svc => svc.GetMembersAsync(s.SurveyId, null));
            Assert.Equal(3, members.Count);                               // the inactive member is not copied
            Assert.DoesNotContain(members, m => m.MemberName == "Former Member");
            Assert.All(members, m => Assert.Null(m.Attended));            // nothing recorded yet

            // The internal members are told; the external one is not, yet.
            var bell = await BellAsync(s.SurveyId, SurveyEvents.CommitteeAssigned);
            Assert.Equal(2, bell.Count);
            // A member who can open the Surveys page goes there; one who cannot goes to the arrival.
            Assert.Equal("/Surveys/Details/" + s.SurveyId,
                bell.Single(b => b.UserId == TestSurveyRecipients.StandIn(InsuranceUser)).LinkUrl);
            Assert.Equal("/Arrivals/Details/" + s.ArrivalId,
                bell.Single(b => b.UserId == TestSurveyRecipients.StandIn(ClaimsUser)).LinkUrl);
            // Only the member who takes e-mail gets one.
            var mail = Assert.Single(await MailsAsync(s.SurveyId, SurveyEvents.CommitteeAssigned));
            Assert.Equal(TestSurveyRecipients.MemberAddress(InsuranceUser), mail.ToAddress);

            // Now change the setup every way there is.
            using (var c = SurveyTestData.Db(_factory))
            {
                await c.ExecuteAsync(@"
                    UPDATE qms_survey_committee_member SET member_name = 'Renamed' WHERE committee_id = @committeeId AND user_id = @u;
                    DELETE FROM qms_survey_committee_member WHERE committee_id = @committeeId AND participant_type = 'External';",
                    new { committeeId, u = InsuranceUser });
            }
            Assert.True((await Committee(svc => svc.DeleteCommitteeAsync(committeeId, "test"))).ok);

            var after = await Committee(svc => svc.GetMembersAsync(s.SurveyId, null));
            Assert.Equal(3, after.Count);
            Assert.Contains(after, m => m.MemberName == "Insurance Person");
            Assert.Contains(after, m => m.MemberName == "Outside Surveyor");
            Assert.Equal("Test committee", (await GetAsync(s.SurveyId))!.CommitteeName);
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    [Fact]
    public async Task With_no_committee_set_up_the_survey_is_still_confirmed_and_can_be_assigned_later()
    {
        // BarePlant has no committee of its own. If the live system has a
        // DEFAULT committee, that is what a survey here correctly gets, and
        // the "none configured" path cannot be reached -- so check first.
        var fallback = await Committee(svc => svc.FindForPlantAsync(BarePlant));
        var s = await SurveyAsync(plant: BarePlant);
        try
        {
            var survey = await GetAsync(s.SurveyId);
            Assert.Equal(SurveyStatus.Confirm, survey!.StatusCode);       // never blocked by the missing committee
            if (fallback != null)
            {
                Assert.Equal(fallback.CommitteeId, survey.CommitteeId);
                return;
            }

            Assert.Null(survey.CommitteeId);
            Assert.Contains("No committee is assigned to this survey",
                await Client(RoleCodes.Admin).GetStringAsync("/Surveys/Details/" + s.SurveyId));
            var none = await Committee(svc => svc.AssignNowAsync(s.SurveyId, "test"));
            Assert.False(none.ok);
            Assert.Contains("No committee is configured", none.error);

            await SeedCommitteeAsync(BarePlant, "Set up afterwards");
            var now = await Committee(svc => svc.AssignNowAsync(s.SurveyId, "test"));
            Assert.True(now.ok, now.error);
            Assert.Equal("Set up afterwards", (await GetAsync(s.SurveyId))!.CommitteeName);
            Assert.Equal(3, (await Committee(svc => svc.GetMembersAsync(s.SurveyId, null))).Count);
            // Assigned once.
            Assert.False((await Committee(svc => svc.AssignNowAsync(s.SurveyId, "test"))).ok);
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    // ---- only for a confirmed survey --------------------------------

    [Fact]
    public async Task Committee_activities_are_refused_for_an_open_and_for_a_passed_survey()
    {
        await SeedCommitteeAsync(Plant);
        var open = await SurveyAsync(confirm: false);
        var pass = await SurveyAsync(confirm: false);
        try
        {
            Assert.True((await With<ISurveyService, (bool ok, string? error)>(svc =>
                svc.DecideAsync(pass.SurveyId, false, "Not needed after all", "decider"))).ok);

            foreach (var id in new[] { open.SurveyId, pass.SurveyId })
            {
                var day = DateTime.Today.AddDays(2);
                var results = new[]
                {
                    await Committee(svc => svc.SaveAppointmentAsync(id, Appointment(day), "test")),
                    await Committee(svc => svc.ConfirmScheduleAsync(id, "test")),
                    await Committee(svc => svc.RescheduleAsync(id, Appointment(day), "Surveyor unavailable", "test")),
                    await Committee(svc => svc.SaveAttendanceAsync(id,
                        new AttendanceInput { ActualDate = day, ActualTime = "10:30", SurveyorAttended = "true" }, "test")),
                    await Committee(svc => svc.ConfirmAttendanceAsync(id, "test")),
                    await Committee(svc => svc.ConfirmInspectionAsync(id, new InspectionInput
                    { SurveyorAttended = "true", ShipmentInspected = "true", InspectionDate = day, StartTime = "10:30" }, "test")),
                    await Committee(svc => svc.AssignNowAsync(id, "test")),
                };
                Assert.All(results, r => Assert.False(r.ok));
                Assert.All(results, r => Assert.Equal(SurveyCommitteeService.NotConfirmed, r.error));

                Assert.Empty(await Committee(svc => svc.GetAppointmentsAsync(id)));
                Assert.Empty(await Committee(svc => svc.GetMembersAsync(id, null)));
                var survey = await GetAsync(id);
                Assert.Null(survey!.CommitteeId);                          // no committee before confirmation
                Assert.False(survey.InspectionConfirmed);
            }
        }
        finally { await CleanupAsync(open.ArrivalId); await CleanupAsync(pass.ArrivalId); }
    }

    // ---- appointment ------------------------------------------------

    [Fact]
    public async Task Scheduling_tells_people_when_confirmed_and_a_reschedule_keeps_the_history()
    {
        await SeedCommitteeAsync(Plant);
        var s = await SurveyAsync();
        try
        {
            var day = DateTime.Today.AddDays(3);
            Assert.False((await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, new AppointmentInput { PlannedTime = "10:30" }, "scheduler"))).ok);
            Assert.False((await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, new AppointmentInput { PlannedDate = day, PlannedTime = "25:99" }, "scheduler"))).ok);

            // Saved: a draft. Nobody is told until the schedule is confirmed.
            var saved = await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, Appointment(day), "scheduler"));
            Assert.True(saved.ok, saved.error);
            Assert.Empty(await BellAsync(s.SurveyId, SurveyEvents.Scheduled));
            Assert.Empty(await MailsAsync(s.SurveyId, SurveyEvents.Scheduled));
            var survey = await GetAsync(s.SurveyId);
            Assert.Equal(day.Date.AddHours(10).AddMinutes(30), survey!.PlannedAt);
            Assert.Null(survey.ScheduleConfirmedAt);

            // A draft can still be moved.
            Assert.True((await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, Appointment(day, "11:00"), "scheduler"))).ok);
            Assert.Single(await Committee(svc => svc.GetAppointmentsAsync(s.SurveyId)));

            var confirm = await Committee(svc => svc.ConfirmScheduleAsync(s.SurveyId, "scheduler"));
            Assert.True(confirm.ok, confirm.error);
            Assert.False((await Committee(svc => svc.ConfirmScheduleAsync(s.SurveyId, "scheduler"))).ok);

            // Internal members by their own preferences.
            Assert.Equal(2, (await BellAsync(s.SurveyId, SurveyEvents.Scheduled)).Count);
            var mails = await MailsAsync(s.SurveyId, SurveyEvents.Scheduled);
            Assert.Contains(mails, m => m.ToAddress == TestSurveyRecipients.MemberAddress(InsuranceUser));
            Assert.DoesNotContain(mails, m => m.ToAddress == TestSurveyRecipients.MemberAddress(ClaimsUser));
            // External people -- the committee's external member and the surveyor
            // named on the appointment -- get the appointment and nothing else.
            foreach (var address in new[] { ExternalMail, SurveyorMail })
            {
                var ext = Assert.Single(mails.Where(m => m.ToAddress == address));
                Assert.Contains("Cold store 3", ext.BodyHtml);
                Assert.Contains("Bring the seal cutter", ext.BodyHtml);
                Assert.DoesNotContain(s.SurveyNo, ext.Subject + ext.BodyHtml);       // no survey number
                Assert.DoesNotContain("/Surveys/", ext.BodyHtml);                    // no way in
                Assert.DoesNotContain("INTERNAL-ONLY-NOTE", ext.BodyHtml);
                Assert.DoesNotContain("Formal survey needed", ext.BodyHtml);         // no decision comments
                Assert.DoesNotContain("Mould on several pallets", ext.BodyHtml);     // no request reason
            }
            // Internal comments are not mailed to anyone.
            Assert.All(mails, m => Assert.DoesNotContain("INTERNAL-ONLY-NOTE", m.BodyHtml));

            // Told a time: changing it now is a reschedule, not an edit.
            var moved = await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, Appointment(day, "14:00"), "scheduler"));
            Assert.False(moved.ok);
            Assert.Contains("Use Reschedule", moved.error);
            // Other details can still be corrected.
            var detail = Appointment(day, "11:00"); detail.Location = "Cold store 4";
            Assert.True((await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, detail, "scheduler"))).ok);

            Assert.False((await Committee(svc => svc.RescheduleAsync(s.SurveyId, Appointment(day.AddDays(1)), " ", "scheduler"))).ok);
            var again = await Committee(svc => svc.RescheduleAsync(s.SurveyId, Appointment(day.AddDays(1), "09:00"), "Surveyor unavailable", "scheduler"));
            Assert.True(again.ok, again.error);

            var all = await Committee(svc => svc.GetAppointmentsAsync(s.SurveyId));
            Assert.Equal(2, all.Count);
            var current = Assert.Single(all.Where(a => a.IsCurrent));
            Assert.Equal(2, current.SeqNo);
            Assert.True(current.ScheduleConfirmed);
            var old = Assert.Single(all.Where(a => !a.IsCurrent));
            Assert.Equal("Surveyor unavailable", old.SupersedeReason);
            Assert.Equal("Cold store 4", old.Location);
            Assert.Equal(2, (await BellAsync(s.SurveyId, SurveyEvents.Rescheduled)).Count);
            Assert.Contains(await MailsAsync(s.SurveyId, SurveyEvents.Rescheduled), m => m.ToAddress == ExternalMail);

            // Through all of it the survey is still simply Confirmed.
            Assert.Equal(SurveyStatus.Confirm, (await GetAsync(s.SurveyId))!.StatusCode);
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    // ---- attendance -------------------------------------------------

    [Fact]
    public async Task Attendance_is_recorded_for_everyone_by_one_person_and_locks_when_confirmed()
    {
        await SeedCommitteeAsync(Plant);
        var s = await SurveyAsync();
        try
        {
            var day = DateTime.Today.AddDays(-1);
            var noAppointment = await Committee(svc => svc.SaveAttendanceAsync(s.SurveyId,
                new AttendanceInput { ActualDate = day, ActualTime = "10:30", SurveyorAttended = "true" }, "recorder"));
            Assert.False(noAppointment.ok);

            Assert.True((await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, Appointment(day), "scheduler"))).ok);
            Assert.True((await Committee(svc => svc.ConfirmScheduleAsync(s.SurveyId, "scheduler"))).ok);
            var members = await Committee(svc => svc.GetMembersAsync(s.SurveyId, null));
            var insurance = members.Single(m => m.MemberName == "Insurance Person");
            var claims    = members.Single(m => m.MemberName == "Claims Person");
            var external  = members.Single(m => m.MemberName == "Outside Surveyor");

            Assert.False((await Committee(svc => svc.SaveAttendanceAsync(s.SurveyId,
                new AttendanceInput { ActualDate = day, ActualTime = "10:30" }, "recorder"))).ok);       // surveyor unanswered
            Assert.False((await Committee(svc => svc.ConfirmAttendanceAsync(s.SurveyId, "confirmer"))).ok);  // nothing to confirm

            // The surveyor did not come. One person records everyone -- the
            // external member included, who has no account to do it with.
            var absent = await Committee(svc => svc.SaveAttendanceAsync(s.SurveyId, new AttendanceInput
            {
                ActualDate = day, ActualTime = "10:45", ActualLocation = "Cold store 3", SurveyorAttended = "false",
                AttendanceComments = "Waited an hour",
                Members =
                {
                    new MemberAttendanceInput { SurveyMemberId = insurance.SurveyMemberId, Attended = "true" },
                    new MemberAttendanceInput { SurveyMemberId = external.SurveyMemberId, Attended = "false", Comments = "No show" },
                    new MemberAttendanceInput { SurveyMemberId = claims.SurveyMemberId, Attended = "" },
                    new MemberAttendanceInput { SurveyMemberId = -999, Attended = "true" },          // not on this survey: ignored
                }
            }, "recorder"));
            Assert.True(absent.ok, absent.error);

            var visit = (await Committee(svc => svc.GetAppointmentsAsync(s.SurveyId))).Single();
            Assert.False(visit.SurveyorAttended);
            Assert.Equal("recorder", visit.AttendanceEnteredBy);
            var recorded = await Committee(svc => svc.GetMembersAsync(s.SurveyId, visit.AppointmentId));
            Assert.True(recorded.Single(m => m.SurveyMemberId == insurance.SurveyMemberId).Attended);
            Assert.False(recorded.Single(m => m.SurveyMemberId == external.SurveyMemberId).Attended);
            // Not recorded is not absent.
            Assert.Null(recorded.Single(m => m.SurveyMemberId == claims.SurveyMemberId).Attended);

            // The members are told, and the people who schedule are told there was a no-show.
            Assert.Equal(2, (await BellAsync(s.SurveyId, SurveyEvents.AttendanceRecorded)).Count);
            var noShow = Assert.Single(await BellAsync(s.SurveyId, SurveyEvents.SurveyorAbsent));
            Assert.Equal(TestSurveyRecipients.DeciderId, noShow.UserId);

            // Corrected before it is confirmed: allowed, and nobody is told twice.
            Assert.True((await Committee(svc => svc.SaveAttendanceAsync(s.SurveyId, new AttendanceInput
            {
                ActualDate = day, ActualTime = "10:45", SurveyorAttended = "false",
                Members = { new MemberAttendanceInput { SurveyMemberId = claims.SurveyMemberId, Attended = "true" } }
            }, "other"))).ok);
            Assert.Equal(2, (await BellAsync(s.SurveyId, SurveyEvents.AttendanceRecorded)).Count);
            Assert.Equal("recorder", (await Committee(svc => svc.GetAppointmentsAsync(s.SurveyId))).Single().AttendanceEnteredBy);

            var confirm = await Committee(svc => svc.ConfirmAttendanceAsync(s.SurveyId, "confirmer"));
            Assert.True(confirm.ok, confirm.error);
            var locked = await Committee(svc => svc.SaveAttendanceAsync(s.SurveyId,
                new AttendanceInput { ActualDate = day, ActualTime = "10:45", SurveyorAttended = "true" }, "recorder"));
            Assert.False(locked.ok);
            Assert.Contains("can no longer be changed", locked.error);
            Assert.False((await Committee(svc => svc.ConfirmAttendanceAsync(s.SurveyId, "confirmer"))).ok);

            // A no-show is rescheduled: the old visit keeps its attendance, the new one starts clean.
            Assert.True((await Committee(svc => svc.RescheduleAsync(s.SurveyId, Appointment(DateTime.Today.AddDays(2)), "Surveyor did not attend", "scheduler"))).ok);
            var fresh = (await Committee(svc => svc.GetAppointmentsAsync(s.SurveyId))).Single(a => a.IsCurrent);
            Assert.False(fresh.AttendanceEntered);
            Assert.All(await Committee(svc => svc.GetMembersAsync(s.SurveyId, fresh.AppointmentId)), m => Assert.Null(m.Attended));
            Assert.True((await Committee(svc => svc.GetMembersAsync(s.SurveyId, visit.AppointmentId)))
                .Single(m => m.SurveyMemberId == insurance.SurveyMemberId).Attended);

            Assert.Equal(SurveyStatus.Confirm, (await GetAsync(s.SurveyId))!.StatusCode);
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    // ---- inspection -------------------------------------------------

    [Fact]
    public async Task The_qc_officer_confirms_the_inspection_once_and_the_survey_stays_confirmed()
    {
        await SeedCommitteeAsync(Plant);
        var s = await SurveyAsync(qoStatus: QualityOrderStatus.Closed);
        try
        {
            var day = DateTime.Today;
            // It is a confirmation that the inspection HAPPENED.
            var notInspected = await Committee(svc => svc.ConfirmInspectionAsync(s.SurveyId, new InspectionInput
            { SurveyorAttended = "true", ShipmentInspected = "false", InspectionDate = day, StartTime = "09:00" }, "qc"));
            Assert.False(notInspected.ok);
            Assert.False((await Committee(svc => svc.ConfirmInspectionAsync(s.SurveyId, new InspectionInput
            { SurveyorAttended = "true", ShipmentInspected = "true", InspectionDate = day, StartTime = "11:00", EndTime = "09:00" }, "qc"))).ok);
            Assert.False((await GetAsync(s.SurveyId))!.InspectionConfirmed);

            // Recording attendance does not confirm the inspection ...
            Assert.True((await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, Appointment(day, "09:00"), "scheduler"))).ok);
            Assert.True((await Committee(svc => svc.SaveAttendanceAsync(s.SurveyId,
                new AttendanceInput { ActualDate = day, ActualTime = "09:00", SurveyorAttended = "true" }, "recorder"))).ok);
            var before = await GetAsync(s.SurveyId);
            Assert.False(before!.InspectionConfirmed);
            Assert.True(before.VisitSurveyorAttended);

            // ... the QC officer does, separately.
            var done = await Committee(svc => svc.ConfirmInspectionAsync(s.SurveyId, new InspectionInput
            {
                SurveyorAttended = "true", ShipmentInspected = "true", InspectionDate = day, StartTime = "09:15", EndTime = "10:40",
                Findings = "Decay on about a fifth of the cartons", Observations = "Pallets near the door worst"
            }, "qc"));
            Assert.True(done.ok, done.error);
            var after = await GetAsync(s.SurveyId);
            Assert.True(after!.InspectionConfirmed);
            Assert.Equal("qc", after.InspectionConfirmedBy);
            Assert.Equal(new TimeSpan(9, 15, 0), after.InspectionStartTime);
            Assert.Equal("Decay on about a fifth of the cartons", after.InspectionFindings);

            // Once.
            var twice = await Committee(svc => svc.ConfirmInspectionAsync(s.SurveyId, new InspectionInput
            { SurveyorAttended = "true", ShipmentInspected = "true", InspectionDate = day, StartTime = "12:00" }, "other"));
            Assert.False(twice.ok);
            Assert.Equal("qc", (await GetAsync(s.SurveyId))!.InspectionConfirmedBy);

            // Insurance and claims members are told; nobody else.
            var told = await BellAsync(s.SurveyId, SurveyEvents.InspectionConfirmed);
            Assert.Equal(2, told.Count);
            Assert.Contains(told, b => b.UserId == TestSurveyRecipients.StandIn(InsuranceUser));
            Assert.Contains(told, b => b.UserId == TestSurveyRecipients.StandIn(ClaimsUser));

            // The status has not moved, the claim was neither approved nor
            // touched by any of this, and the banner is still on the order.
            Assert.Equal(SurveyStatus.Confirm, after.StatusCode);
            using (var c = SurveyTestData.Db(_factory))
                Assert.Null(await c.ExecuteScalarAsync<DateTime?>(
                    "SELECT decided_at FROM qms_claim WHERE quality_order_id = @id", new { id = s.QoId }));
            Assert.Contains("UNDER SURVEY", await Client(RoleCodes.Admin).GetStringAsync("/QualityOrders/Details/" + s.QoId));
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    [Fact]
    public async Task Each_committee_act_is_its_own_permission()
    {
        var perms = _factory.Services.GetRequiredService<IPermissionResolver>();
        await perms.EnsureLoadedAsync();
        bool Can(string role, string code) => perms.RoleHas(role, code, AccessLevel.Edit, isScreen: false);

        foreach (var code in new[] { Perm.Surveys.Schedule, Perm.Surveys.Attendance, Perm.Surveys.ConfirmAttendance, Perm.Surveys.Attachments })
        {
            foreach (var r in new[] { RoleCodes.Admin, RoleCodes.Manager, RoleCodes.ClaimManager }) Assert.True(Can(r, code), r + " / " + code);
            foreach (var r in new[] { RoleCodes.Supervisor, RoleCodes.Operator, RoleCodes.Viewer }) Assert.False(Can(r, code), r + " / " + code);
        }
        // The inspection confirmation is QC's: the claim manager does not hold it.
        Assert.True(Can(RoleCodes.Manager, Perm.Surveys.ConfirmInspection));
        Assert.False(Can(RoleCodes.ClaimManager, Perm.Surveys.ConfirmInspection));
        Assert.False(Can(RoleCodes.Supervisor, Perm.Surveys.ConfirmInspection));

        // Posting straight at the endpoint without the permission changes nothing.
        await SeedCommitteeAsync(Plant);
        var s = await SurveyAsync();
        try
        {
            var op = Client(RoleCodes.Operator);
            var token = await TokenAsync(op, "/Arrivals/Details/" + s.ArrivalId);
            foreach (var action in new[] { "SaveAppointment", "ConfirmSchedule", "SaveAttendance", "ConfirmAttendance", "ConfirmInspection", "AssignCommittee" })
                await op.PostAsync($"/Surveys/{action}/{s.SurveyId}", new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("__RequestVerificationToken", token),
                    new KeyValuePair<string, string>("PlannedDate", DateTime.Today.ToString("yyyy-MM-dd")),
                    new KeyValuePair<string, string>("PlannedTime", "10:00"),
                    new KeyValuePair<string, string>("ActualDate", DateTime.Today.ToString("yyyy-MM-dd")),
                    new KeyValuePair<string, string>("ActualTime", "10:00"),
                    new KeyValuePair<string, string>("SurveyorAttended", "true"),
                    new KeyValuePair<string, string>("ShipmentInspected", "true"),
                    new KeyValuePair<string, string>("InspectionDate", DateTime.Today.ToString("yyyy-MM-dd")),
                    new KeyValuePair<string, string>("StartTime", "10:00"),
                }));
            Assert.Empty(await Committee(svc => svc.GetAppointmentsAsync(s.SurveyId)));
            Assert.False((await GetAsync(s.SurveyId))!.InspectionConfirmed);
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    // ---- milestones -------------------------------------------------

    [Fact]
    public async Task The_milestones_follow_the_records_and_are_never_a_status()
    {
        await SeedCommitteeAsync(Plant);
        var s = await SurveyAsync();
        try
        {
            async Task<bool> In(string milestone) =>
                (await With<ISurveyService, SurveyPage>(svc => svc.ListAsync(
                    new SurveyListFilter { Search = s.SurveyNo, Milestone = milestone }, PlantScope.All))).Rows.Count == 1;

            Assert.True(await In(SurveyMilestone.AwaitingSchedule));
            Assert.False(await In(SurveyMilestone.AwaitingAttendance));
            Assert.False(await In(SurveyMilestone.AwaitingInspection));

            // An appointment in the past with no attendance: waiting for it to be recorded.
            var day = DateTime.Today.AddDays(-1);
            Assert.True((await Committee(svc => svc.SaveAppointmentAsync(s.SurveyId, Appointment(day), "scheduler"))).ok);
            Assert.False(await In(SurveyMilestone.AwaitingSchedule));
            Assert.True(await In(SurveyMilestone.AwaitingAttendance));

            Assert.True((await Committee(svc => svc.SaveAttendanceAsync(s.SurveyId,
                new AttendanceInput { ActualDate = day, ActualTime = "10:30", SurveyorAttended = "true" }, "recorder"))).ok);
            Assert.False(await In(SurveyMilestone.AwaitingAttendance));
            Assert.True(await In(SurveyMilestone.AwaitingInspection));

            Assert.True((await Committee(svc => svc.ConfirmInspectionAsync(s.SurveyId, new InspectionInput
            { SurveyorAttended = "true", ShipmentInspected = "true", InspectionDate = day, StartTime = "10:30" }, "qc"))).ok);
            Assert.False(await In(SurveyMilestone.AwaitingInspection));

            // Finished with all of it, and the status is what it always was.
            Assert.Equal(SurveyStatus.Confirm, (await GetAsync(s.SurveyId))!.StatusCode);
            var list = await Client(RoleCodes.Admin).GetStringAsync("/Surveys?search=" + s.SurveyNo);
            Assert.Contains("Test committee", list);

            var counts = await With<ISurveyService, SurveyCounts>(svc => svc.CountsAsync(PlantScope.All));
            Assert.Equal(counts.Total, counts.Open + counts.Confirmed + counts.Passed);
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    // ---- attachments ------------------------------------------------

    private static MultipartFormDataContent Upload(string token, string fileName, string contentType, byte[] bytes,
        params (string Key, string Value)[] fields)
    {
        var form = new MultipartFormDataContent { { new StringContent(token), "__RequestVerificationToken" } };
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "files", fileName);
        return form;
    }

    [Fact]
    public async Task Survey_files_are_served_only_to_people_who_may_see_the_survey()
    {
        var s = await SurveyAsync(qoStatus: QualityOrderStatus.Closed);
        try
        {
            var admin = Client(RoleCodes.Admin);
            var url = "/Surveys/Details/" + s.SurveyId;
            var name = "survey-report-" + Guid.NewGuid().ToString("N")[..8] + ".pdf";
            var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 test survey report");

            var up = await admin.PostAsync("/Surveys/UploadDocument/" + s.SurveyId,
                Upload(await TokenAsync(admin, url), name, "application/pdf", bytes, ("category", SurveyDocCategory.SurveyReport)));
            Assert.Equal(HttpStatusCode.Redirect, up.StatusCode);
            // A photograph is accepted here (and only here); a program is not.
            await admin.PostAsync("/Surveys/UploadDocument/" + s.SurveyId,
                Upload(await TokenAsync(admin, url), "damage.png", "image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, ("category", SurveyDocCategory.Photo)));
            await admin.PostAsync("/Surveys/UploadDocument/" + s.SurveyId,
                Upload(await TokenAsync(admin, url), "tool.exe", "application/octet-stream", new byte[] { 1, 2, 3 }, ("category", SurveyDocCategory.Other)));

            var docs = await With<IDocumentService, IReadOnlyList<SharbatlyQMS.Web.ViewModels.DocumentInfo>>(
                svc => svc.ListAsync(DocumentService.SurveyOwner, s.SurveyId));
            Assert.Equal(2, docs.Count);
            var report = docs.Single(d => d.OriginalName == name);
            Assert.Equal(SurveyDocCategory.SurveyReport, report.Category);
            Assert.Contains(name, await admin.GetStringAsync(url));

            // Read through the survey, by someone who may see it.
            var read = await admin.GetAsync($"/Surveys/Document/{s.SurveyId}?documentId={report.DocumentId}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());

            // Not by someone who may not ...
            var op = Client(RoleCodes.Operator);
            Assert.NotEqual(HttpStatusCode.OK, (await op.GetAsync($"/Surveys/Document/{s.SurveyId}?documentId={report.DocumentId}")).StatusCode);
            // ... not through another survey's address ...
            var other = await SurveyAsync(confirm: false);
            try
            {
                Assert.Equal(HttpStatusCode.NotFound,
                    (await admin.GetAsync($"/Surveys/Document/{other.SurveyId}?documentId={report.DocumentId}")).StatusCode);
            }
            finally { await CleanupAsync(other.ArrivalId); }
            // ... and never through the general document endpoints, whose
            // download right every role holds -- not even for an administrator.
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Documents/Download/" + report.DocumentId)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/Documents/Preview/" + report.DocumentId)).StatusCode);
            // (Refused for the operator too -- as "not found" or, where the live
            // grants have taken the general download right away, as "forbidden".)
            Assert.NotEqual(HttpStatusCode.OK, (await op.GetAsync("/Documents/Download/" + report.DocumentId)).StatusCode);

            // The supplier mail dialog offers the order's and the arrival's
            // documents. A survey's file is neither.
            var send = await admin.GetAsync("/Reports/PrepareSendFromQo/" + s.QoId);
            Assert.DoesNotContain(name, await send.Content.ReadAsStringAsync());
            Assert.Empty(await With<IDocumentService, IReadOnlyList<SharbatlyQMS.Web.ViewModels.DocumentInfo>>(
                svc => svc.ListAsync("QualityOrder", s.QoId!.Value)));
            Assert.Empty(await With<IDocumentService, IReadOnlyList<SharbatlyQMS.Web.ViewModels.DocumentInfo>>(
                svc => svc.ListAsync("Arrival", s.ArrivalId)));

            // Removed by someone allowed to.
            var del = await admin.PostAsync("/Surveys/DeleteDocument/" + s.SurveyId, new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, url)),
                new KeyValuePair<string, string>("documentId", report.DocumentId.ToString()),
            }));
            Assert.Equal(HttpStatusCode.Redirect, del.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await admin.GetAsync($"/Surveys/Document/{s.SurveyId}?documentId={report.DocumentId}")).StatusCode);
        }
        finally { await CleanupAsync(s.ArrivalId); }
    }

    [Fact]
    public async Task Evidence_given_with_the_request_is_kept_with_the_survey_not_the_arrival()
    {
        var (arrivalId, _, _) = await SurveyTestData.SeedAsync(_factory, ArrivalStatus.Draft, null, plant: Plant);
        try
        {
            var op = Client(RoleCodes.Operator);
            var res = await op.PostAsync("/Surveys/RequestSurvey",
                Upload(await TokenAsync(op, "/Arrivals/Details/" + arrivalId), "pallets.jpg", "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF },
                    ("arrivalId", arrivalId.ToString()), ("reason", "Cartons wet and collapsed")));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("\"ok\":true", await res.Content.ReadAsStringAsync());

            var survey = await With<ISurveyService, SurveyBrief?>(svc => svc.GetBriefForArrivalAsync(arrivalId));
            var doc = Assert.Single(await With<IDocumentService, IReadOnlyList<SharbatlyQMS.Web.ViewModels.DocumentInfo>>(
                svc => svc.ListAsync(DocumentService.SurveyOwner, survey!.SurveyId)));
            Assert.Equal("pallets.jpg", doc.OriginalName);
            Assert.Equal(SurveyDocCategory.Request, doc.Category);

            // Nothing was added to the arrival, whose photos print in the QC report.
            using var c = SurveyTestData.Db(_factory);
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_image_link WHERE owner_type = 'Arrival' AND owner_id = @arrivalId", new { arrivalId }));
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM qms_document WHERE owner_type = 'Arrival' AND owner_id = @arrivalId", new { arrivalId }));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- page and deletion ------------------------------------------

    [Fact]
    public async Task The_survey_page_shows_the_committee_sections_and_deleting_the_arrival_removes_them()
    {
        await SeedCommitteeAsync(Plant);
        var (arrivalId, _, _) = await SurveyTestData.SeedAsync(_factory, ArrivalStatus.Draft, null, plant: Plant);
        var deleted = false;
        try
        {
            var res = await With<ISurveyService, SurveyRequestResult>(svc => svc.RequestAsync(arrivalId,
                SurveySource.Arrival, null, "Mould on several pallets", null, TestSurveyRecipients.RequesterName));
            var id = res.SurveyId!.Value;

            var admin = Client(RoleCodes.Admin);
            var open = await admin.GetStringAsync("/Surveys/Details/" + id);
            Assert.Contains("start once this survey is confirmed", open);
            Assert.DoesNotContain("id=\"appointmentModal\"", open);

            Assert.True((await With<ISurveyService, (bool ok, string? error)>(svc => svc.DecideAsync(id, true, null, "decider"))).ok);
            var day = DateTime.Today.AddDays(-1);
            Assert.True((await Committee(svc => svc.SaveAppointmentAsync(id, Appointment(day), "scheduler"))).ok);
            var member = (await Committee(svc => svc.GetMembersAsync(id, null))).First();
            Assert.True((await Committee(svc => svc.SaveAttendanceAsync(id, new AttendanceInput
            {
                ActualDate = day, ActualTime = "10:30", SurveyorAttended = "true",
                Members = { new MemberAttendanceInput { SurveyMemberId = member.SurveyMemberId, Attended = "true" } }
            }, "recorder"))).ok);

            var page = await admin.GetStringAsync("/Surveys/Details/" + id);
            foreach (var expected in new[] { "Test committee", "Insurance Person", "Outside Surveyor", "Survey appointment",
                         "Cold store 3", "Committee attendance", "Survey inspection", "Attachments", "id=\"appointmentModal\"",
                         "Attendance recorded", "Committee assigned" })
                Assert.Contains(expected, page);

            // Everything the committee recorded hangs off the survey, and the
            // survey off the arrival: deleting the arrival must take all of it.
            using (var scope = _factory.Services.CreateScope())
            {
                var (ok, error) = await scope.ServiceProvider.GetRequiredService<IArrivalService>().DeleteAsync(arrivalId, "test");
                Assert.True(ok, error);
                deleted = true;
            }
            using var c = SurveyTestData.Db(_factory);
            Assert.Equal(0, await c.ExecuteScalarAsync<int>(@"
                SELECT (SELECT COUNT(*) FROM qms_survey WHERE survey_id = @id)
                     + (SELECT COUNT(*) FROM qms_survey_member WHERE survey_id = @id)
                     + (SELECT COUNT(*) FROM qms_survey_appointment WHERE survey_id = @id)
                     + (SELECT COUNT(*) FROM qms_notification WHERE entity_type = 'Survey' AND entity_id = @id)
                     + (SELECT COUNT(*) FROM qms_mail_outbox WHERE entity_type = 'Survey' AND entity_id = @id)", new { id }));
            await SurveyTestData.ForgetSurveyAuditAsync(_factory, id);
        }
        finally { if (!deleted) await CleanupAsync(arrivalId); }
    }
}
