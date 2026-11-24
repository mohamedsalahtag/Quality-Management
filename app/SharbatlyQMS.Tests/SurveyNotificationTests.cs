using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Survey management, stage 2: notifications -- the bell, and the mail outbox
/// that makes survey e-mail reliable.
///
/// What is being held: a notice is written with the event it reports and only
/// once however often the event is processed; a mail that fails is logged and
/// retried and never undoes the decision behind it; and a person can only see
/// and mark their own notifications.
///
/// The suite runs against the live database, so two precautions are built in
/// (see SurveyTestDoubles): the test host notifies stand-in recipients, not
/// real users, and parks every message it writes in the year 2099, where the
/// deployed mail worker will never pick it up. For the same reason nothing
/// here calls DispatchDueAsync -- on the live table that would process REAL
/// messages through the test host's recording mail service.
/// </summary>
[Collection("workflow")]
public class SurveyNotificationTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SurveyNotificationTests(QmsAppFactory factory) => _factory = factory;

    // ---- plumbing ---------------------------------------------------

    private Task<(long ArrivalId, long? QoId, string Tag)> SeedAsync(string arrivalStatus, string? qoStatus) =>
        SurveyTestData.SeedAsync(_factory, arrivalStatus, qoStatus);

    private Task CleanupAsync(long arrivalId) => SurveyTestData.CleanupAsync(_factory, arrivalId);

    private async Task<T> With<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        using var s = _factory.Services.CreateScope();
        return await work(s.ServiceProvider.GetRequiredService<TService>());
    }

    private async Task<SurveyRequestResult> RequestAsync(long arrivalId, long? qoId = null)
    {
        var res = await With<ISurveyService, SurveyRequestResult>(svc => svc.RequestAsync(arrivalId,
            qoId.HasValue ? SurveySource.QualityOrder : SurveySource.Arrival, qoId,
            "Mould on several pallets", null, TestSurveyRecipients.RequesterName));
        Assert.True(res.Ok, res.Error);
        return res;
    }

    private async Task DecideAsync(long surveyId, bool confirm)
    {
        var (ok, error) = await With<ISurveyService, (bool ok, string? error)>(svc =>
            svc.DecideAsync(surveyId, confirm, confirm ? "Formal survey needed" : "Only one damaged carton", "decider"));
        Assert.True(ok, error);
    }

    private sealed class BellRow
    {
        public long      NotificationId { get; set; }
        public int       UserId         { get; set; }
        public string    EventCode      { get; set; } = "";
        public string    Title          { get; set; } = "";
        public string?   LinkUrl        { get; set; }
        public DateTime? ReadAt         { get; set; }
    }

    private async Task<List<BellRow>> BellAsync(long surveyId)
    {
        using var c = SurveyTestData.Db(_factory);
        return (await c.QueryAsync<BellRow>(@"
            SELECT notification_id AS NotificationId, user_id AS UserId, event_code AS EventCode,
                   title AS Title, link_url AS LinkUrl, read_at AS ReadAt
            FROM   qms_notification
            WHERE  entity_type = 'Survey' AND entity_id = @surveyId
            ORDER  BY notification_id", new { surveyId })).ToList();
    }

    private Task<IReadOnlyList<OutboxRow>> MailsAsync(long surveyId) =>
        With<IMailOutbox, IReadOnlyList<OutboxRow>>(svc => svc.ListForEntityAsync(EntityTypes.Survey, surveyId));

    /// <summary>A mail service that always refuses, to exercise the failure path.</summary>
    private sealed class RefusingEmail : IEmailService
    {
        public int Calls;
        public Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        { Calls++; return Task.FromResult(false); }
        public Task<(bool ok, string? error)> SendWithAttachmentsAsync(IEnumerable<string> to, IEnumerable<string> cc,
            string subject, string body, bool bodyIsHtml,
            IEnumerable<(byte[] bytes, string fileName, string mediaType)> attachments, CancellationToken ct = default)
            => Task.FromResult<(bool, string?)>((false, "refused"));
        public Task<bool> SendToEmailGroupAsync(int groupId, string subject, string htmlBody, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<(bool ok, string message)> SendTestEmailAsync(string toEmail, CancellationToken ct = default)
            => Task.FromResult((false, "refused"));
    }

    private ParkedMailOutbox OutboxWith(IEmailService email)
    {
        using var s = _factory.Services.CreateScope();
        return new ParkedMailOutbox(s.ServiceProvider.GetRequiredService<IConfiguration>(), email,
            NullLogger<MailOutbox>.Instance);
    }

    // ---- what gets written ------------------------------------------

    [Fact]
    public async Task A_request_tells_the_deciders_once_by_bell_and_by_mail()
    {
        var (arrivalId, _, tag) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var res = await RequestAsync(arrivalId);
            var id = res.SurveyId!.Value;

            var bell = Assert.Single(await BellAsync(id));
            Assert.Equal(TestSurveyRecipients.DeciderId, bell.UserId);
            Assert.Equal(SurveyEvents.Requested, bell.EventCode);
            Assert.Contains(res.SurveyNo!, bell.Title);
            Assert.Equal("/Surveys/Details/" + id, bell.LinkUrl);
            Assert.Null(bell.ReadAt);

            var mail = Assert.Single(await MailsAsync(id));
            Assert.Equal(TestSurveyRecipients.Decider.Email, mail.ToAddress);
            Assert.Contains(res.SurveyNo!, mail.Subject);
            Assert.Contains(tag, mail.Subject);
            Assert.Equal(OutboxStatus.Pending, mail.StatusCode);
            Assert.Equal(0, mail.Attempts);
            // Parked: the deployed worker only sends what is due.
            Assert.True(mail.NextAttemptAt.Year >= 2099);

            // Asking again creates no survey, and no second notice.
            Assert.True((await RequestAsync(arrivalId)).AlreadyExisted);
            Assert.Single(await BellAsync(id));
            Assert.Single(await MailsAsync(id));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_decision_tells_whoever_asked_and_the_other_deciders()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var id = (await RequestAsync(arrivalId)).SurveyId!.Value;
            await DecideAsync(id, confirm: true);

            var decided = (await BellAsync(id)).Where(b => b.EventCode == SurveyEvents.Confirmed).ToList();
            Assert.Equal(2, decided.Count);
            // The requester may not be able to open the survey, so theirs
            // leads to the arrival, which they can.
            Assert.Equal("/Arrivals/Details/" + arrivalId,
                decided.Single(b => b.UserId == TestSurveyRecipients.RequesterId).LinkUrl);
            Assert.Equal("/Surveys/Details/" + id,
                decided.Single(b => b.UserId == TestSurveyRecipients.DeciderId).LinkUrl);
            Assert.All(decided, b => Assert.Contains("was confirmed", b.Title));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task An_automatic_claim_request_tells_the_claim_reviewers_once()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Closed);
        try
        {
            var id = (await RequestAsync(arrivalId, qoId)).SurveyId!.Value;
            await DecideAsync(id, confirm: true);      // order already finished: raised at once

            var raised = Assert.Single((await BellAsync(id)).Where(b => b.EventCode == SurveyEvents.ClaimRaised));
            Assert.Equal("/ClaimManagement/Details/" + qoId, raised.LinkUrl);
            Assert.Single((await MailsAsync(id)).Where(m => m.EventCode == SurveyEvents.ClaimRaised));

            // Running the claim step again raises nothing and notifies nobody.
            await With<ISurveyClaimSync, ClaimSyncResult>(svc => svc.AfterOrderFinishedAsync(qoId!.Value));
            await With<ISurveyService, string?>(svc => svc.RunFollowUpsAsync(id, "retry"));
            Assert.Single((await BellAsync(id)).Where(b => b.EventCode == SurveyEvents.ClaimRaised));
            Assert.Single((await MailsAsync(id)).Where(m => m.EventCode == SurveyEvents.ClaimRaised));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_passed_survey_raises_no_claim_notice()
    {
        var (arrivalId, qoId, _) = await SeedAsync(ArrivalStatus.Completed, QualityOrderStatus.Closed);
        try
        {
            var id = (await RequestAsync(arrivalId, qoId)).SurveyId!.Value;
            await DecideAsync(id, confirm: false);
            var bell = await BellAsync(id);
            Assert.DoesNotContain(bell, b => b.EventCode == SurveyEvents.ClaimRaised);
            Assert.Contains(bell, b => b.EventCode == SurveyEvents.Passed);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- delivery ---------------------------------------------------

    [Fact]
    public async Task A_message_is_sent_once_and_logged_as_sent()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var res = await RequestAsync(arrivalId);
            var mail = Assert.Single(await MailsAsync(res.SurveyId!.Value));

            Assert.True(await With<IMailOutbox, bool>(svc => svc.DispatchOneAsync(mail.OutboxId)));

            var sent = await With<IMailOutbox, OutboxRow?>(svc => svc.GetAsync(mail.OutboxId));
            Assert.Equal(OutboxStatus.Sent, sent!.StatusCode);
            Assert.NotNull(sent.SentAt);
            Assert.Equal(1, sent.Attempts);
            Assert.Contains(RecordingEmailService.Instance.Outbox,
                m => m.Subject == mail.Subject && m.To.Contains(TestSurveyRecipients.Decider.Email!));

            // A sent message is not sent again.
            Assert.False(await With<IMailOutbox, bool>(svc => svc.DispatchOneAsync(mail.OutboxId)));
            Assert.Equal(1, (await With<IMailOutbox, OutboxRow?>(svc => svc.GetAsync(mail.OutboxId)))!.Attempts);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_failed_mail_is_logged_and_retried_and_never_undoes_the_decision()
    {
        var (arrivalId, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var id = (await RequestAsync(arrivalId)).SurveyId!.Value;
            await DecideAsync(id, confirm: true);
            var mail = Assert.Single(await MailsAsync(id));

            var refusing = new RefusingEmail();
            var outbox = OutboxWith(refusing);

            // Each refusal is counted and explained; the message stays queued.
            for (var attempt = 1; attempt < MailOutbox.MaxAttempts; attempt++)
            {
                Assert.False(await outbox.DispatchOneAsync(mail.OutboxId));
                var row = await outbox.GetAsync(mail.OutboxId);
                Assert.Equal(OutboxStatus.Pending, row!.StatusCode);
                Assert.Equal(attempt, row.Attempts);
                Assert.False(string.IsNullOrWhiteSpace(row.LastError));
                Assert.True(row.NextAttemptAt.Year >= 2099);      // still out of the deployed worker's reach
            }
            // After the last attempt it is Failed, for a person to resend.
            Assert.False(await outbox.DispatchOneAsync(mail.OutboxId));
            Assert.Equal(OutboxStatus.Failed, (await outbox.GetAsync(mail.OutboxId))!.StatusCode);
            Assert.Equal(MailOutbox.MaxAttempts, refusing.Calls);

            // None of that touched the decision.
            var survey = await With<ISurveyService, Survey?>(svc => svc.GetAsync(id));
            Assert.Equal(SurveyStatus.Confirm, survey!.StatusCode);

            // Resend, with a mail server that works again.
            Assert.True(await With<IMailOutbox, bool>(svc => svc.DispatchOneAsync(mail.OutboxId)));
            Assert.Equal(OutboxStatus.Sent, (await outbox.GetAsync(mail.OutboxId))!.StatusCode);
        }
        finally { await CleanupAsync(arrivalId); }
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

    [Fact]
    public async Task The_survey_page_shows_its_mail_log_and_can_send_a_message_now()
    {
        var (a1, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        var (a2, _, _) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var s1 = (await RequestAsync(a1)).SurveyId!.Value;
            var s2 = (await RequestAsync(a2)).SurveyId!.Value;
            var mail1 = Assert.Single(await MailsAsync(s1));
            var mail2 = Assert.Single(await MailsAsync(s2));

            var admin = Client(RoleCodes.Admin);
            var url = "/Surveys/Details/" + s1;
            var page = await admin.GetStringAsync(url);
            Assert.Contains("Notification log", page);
            Assert.Contains(TestSurveyRecipients.Decider.Email!, page);

            // A message of a DIFFERENT survey cannot be sent through this one.
            var wrong = await admin.PostAsync("/Surveys/ResendMail/" + s1, new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, url)),
                new KeyValuePair<string, string>("outboxId", mail2.OutboxId.ToString()),
            }));
            Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
            Assert.Equal(OutboxStatus.Pending, (await With<IMailOutbox, OutboxRow?>(svc => svc.GetAsync(mail2.OutboxId)))!.StatusCode);

            var ok = await admin.PostAsync("/Surveys/ResendMail/" + s1, new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(admin, url)),
                new KeyValuePair<string, string>("outboxId", mail1.OutboxId.ToString()),
            }));
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
            Assert.Equal(OutboxStatus.Sent, (await With<IMailOutbox, OutboxRow?>(svc => svc.GetAsync(mail1.OutboxId)))!.StatusCode);
        }
        finally { await CleanupAsync(a1); await CleanupAsync(a2); }
    }

    // ---- the bell ---------------------------------------------------

    [Fact]
    public async Task The_bell_shows_only_your_own_notifications_and_opening_one_marks_it_read()
    {
        var (arrivalId, _, tag) = await SeedAsync(ArrivalStatus.Draft, null);
        try
        {
            var id = (await RequestAsync(arrivalId)).SurveyId!.Value;

            // Two notifications for the non-administrator test user (id 0),
            // hung on this survey so the cleanup removes them.
            const int me = 0;
            var title = "Test notice " + tag;
            using (var c = SurveyTestData.Db(_factory))
            {
                await c.OpenAsync();
                using var tx = c.BeginTransaction();
                using var scope = _factory.Services.CreateScope();
                var bell = scope.ServiceProvider.GetRequiredService<INotificationService>();
                foreach (var n in new[] { "a", "b" })
                    await bell.AddAsync(c, tx, new AppNotification
                    {
                        EventCode = "Test", Title = title + " " + n, Body = "Body " + n,
                        EntityType = EntityTypes.Survey, EntityId = id, DedupeKey = $"test:{tag}:{n}"
                    }, new[] { new NotificationTarget(me, "/Arrivals/Details/" + arrivalId) });
                // The same event again: still one row each.
                await bell.AddAsync(c, tx, new AppNotification
                {
                    EventCode = "Test", Title = title + " a", EntityType = EntityTypes.Survey, EntityId = id,
                    DedupeKey = $"test:{tag}:a"
                }, new[] { new NotificationTarget(me, null) });
                tx.Commit();
            }
            var mine = (await BellAsync(id)).Where(b => b.UserId == me).ToList();
            Assert.Equal(2, mine.Count);

            var op = Client(RoleCodes.Operator);                  // user id 0
            var admin = Client(RoleCodes.Admin);                  // a different user

            var home = await op.GetStringAsync("/Inbox");
            Assert.Contains(title + " a", home);
            Assert.Contains("id=\"notifBell\"", home);           // the bell is in the top bar
            Assert.Contains(title, await op.GetStringAsync("/Inbox/Recent"));
            // Somebody else sees none of it.
            Assert.DoesNotContain(title, await admin.GetStringAsync("/Inbox"));
            Assert.DoesNotContain(title, await admin.GetStringAsync("/Inbox/Recent"));

            // Opening follows the link and marks that one read -- only for its owner.
            var notMine = await admin.GetAsync("/Inbox/Open/" + mine[0].NotificationId);
            Assert.Equal(HttpStatusCode.Redirect, notMine.StatusCode);
            Assert.DoesNotContain("/Arrivals/Details/", notMine.Headers.Location!.ToString());
            Assert.Null((await BellAsync(id)).Single(b => b.NotificationId == mine[0].NotificationId).ReadAt);

            var opened = await op.GetAsync("/Inbox/Open/" + mine[0].NotificationId);
            Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
            Assert.EndsWith("/Arrivals/Details/" + arrivalId, opened.Headers.Location!.ToString());
            var after = await BellAsync(id);
            Assert.NotNull(after.Single(b => b.NotificationId == mine[0].NotificationId).ReadAt);
            Assert.Null(after.Single(b => b.NotificationId == mine[1].NotificationId).ReadAt);

            // Mark all as read.
            var all = await op.PostAsync("/Inbox/MarkAllRead", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", await TokenAsync(op, "/Inbox")),
            }));
            Assert.Equal(HttpStatusCode.Redirect, all.StatusCode);
            Assert.All((await BellAsync(id)).Where(b => b.UserId == me), b => Assert.NotNull(b.ReadAt));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    // ---- who is really told (the production lookup, read-only) -------

    [Fact]
    public async Task The_real_lookup_returns_active_non_administrators_of_the_branch_who_hold_the_permission()
    {
        var real = new SurveyRecipients();
        using var c = SurveyTestData.Db(_factory);
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var deciders = await real.WithPermissionAsync(c, tx, Perm.Surveys.Decide, "JD01");
        foreach (var d in deciders)
        {
            var who = await c.QuerySingleAsync<(string RoleCode, bool IsActive, int Plants, int Grants)>(@"
                SELECT u.RoleCode, u.IsActive,
                       (SELECT COUNT(*) FROM portal.UserPlant up WHERE up.UserId = u.UserId AND up.Plant = 'JD01'),
                       (SELECT COUNT(*) FROM qms_role_permission rp
                         WHERE rp.role_code = u.RoleCode AND rp.permission_code = @code)
                FROM   qms.AppUser u WHERE u.UserId = @id",
                new { id = d.UserId, code = Perm.Surveys.Decide }, tx);
            Assert.NotEqual(RoleCodes.Admin, who.RoleCode);        // administrators are left out
            Assert.True(who.IsActive);
            Assert.Equal(1, who.Plants);
            Assert.Equal(1, who.Grants);
        }

        // Nobody for a branch that does not exist, or a permission that does not.
        Assert.Empty(await real.WithPermissionAsync(c, tx, Perm.Surveys.Decide, "ZZ99"));
        Assert.Empty(await real.WithPermissionAsync(c, tx, "No.Such.Permission", null));

        Assert.Equal(QmsAppFactory.AdminUserId,
            (await real.ByUsernameAsync(c, tx, QmsAppFactory.AdminUser))!.UserId);
        Assert.Null(await real.ByUsernameAsync(c, tx, "no-such-user-" + Guid.NewGuid().ToString("N")));
        Assert.Null(await real.ByUsernameAsync(c, tx, " "));
    }
}
