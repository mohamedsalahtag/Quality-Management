using System.Net;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Tells people about survey events: a bell notification for application
/// users and, for the events that need somebody to act, an e-mail through the
/// outbox.
///
/// Every method is called INSIDE the transaction of the event it reports, so
/// what it writes commits or rolls back with that event. It never breaks that
/// transaction: its work sits behind a savepoint, and a failure here is
/// logged and undone to the savepoint while the business change goes ahead.
///
/// Nothing here ever addresses a supplier.
/// </summary>
public interface ISurveyNotifier
{
    /// <summary>A survey was requested: the people who may decide it.</summary>
    Task RequestedAsync(SqlConnection c, SqlTransaction tx, long surveyId, string actor);
    /// <summary>A survey was confirmed or passed: whoever asked, and the other deciders.</summary>
    Task DecidedAsync(SqlConnection c, SqlTransaction tx, long surveyId, string actor);
    /// <summary>The claim step raised a claim request: the people who review claims.</summary>
    Task ClaimRaisedAsync(SqlConnection c, SqlTransaction tx, long surveyId);
}

public static class SurveyEvents
{
    public const string Requested   = "SurveyRequested";
    public const string Confirmed   = "SurveyConfirmed";
    public const string Passed      = "SurveyPassed";
    public const string ClaimRaised = "SurveyClaimRaised";
}

public class SurveyNotifier : ISurveyNotifier
{
    private readonly INotificationService _bell;
    private readonly IMailOutbox _outbox;
    private readonly ISurveyRecipients _recipients;
    private readonly ISettingsService _settings;
    private readonly ILogger<SurveyNotifier> _log;

    public SurveyNotifier(INotificationService bell, IMailOutbox outbox, ISurveyRecipients recipients,
        ISettingsService settings, ILogger<SurveyNotifier> log)
    {
        _bell = bell; _outbox = outbox; _recipients = recipients; _settings = settings; _log = log;
    }

    private sealed class Ctx
    {
        public long    SurveyId         { get; set; }
        public string  SurveyNo         { get; set; } = "";
        public string  StatusCode       { get; set; } = "";
        public string  RequestReason    { get; set; } = "";
        public string  RequestedBy      { get; set; } = "";
        public string? DecidedBy        { get; set; }
        public string? DecisionComments { get; set; }
        public long    ArrivalId        { get; set; }
        public string  ArrivalNo        { get; set; } = "";
        public string? ContainerNo      { get; set; }
        public string? VendorName       { get; set; }
        public string? Plant            { get; set; }
        public long?   QoId             { get; set; }
        public string? QoNo             { get; set; }
    }

    private static Task<Ctx?> LoadAsync(SqlConnection c, SqlTransaction tx, long surveyId) =>
        c.QuerySingleOrDefaultAsync<Ctx>(@"
            SELECT s.survey_id AS SurveyId, s.survey_no AS SurveyNo, s.status_code AS StatusCode,
                   s.request_reason AS RequestReason, s.requested_by AS RequestedBy,
                   s.decided_by AS DecidedBy, s.decision_comments AS DecisionComments,
                   a.arrival_id AS ArrivalId, a.arrival_no AS ArrivalNo, a.container_no AS ContainerNo,
                   a.vendor_name AS VendorName, a.plant AS Plant,
                   qo.quality_order_id AS QoId, qo.quality_order_no AS QoNo
            FROM   qms_survey s
            JOIN   qms_arrival a ON a.arrival_id = s.arrival_id
            OUTER  APPLY (
                SELECT TOP 1 q.quality_order_id, q.quality_order_no
                FROM   qms_quality_order q
                WHERE  q.arrival_id = a.arrival_id AND q.status_code <> 'Cancelled'
                  AND  q.superseded_at IS NULL
                ORDER  BY q.quality_order_id DESC
            ) qo
            WHERE  s.survey_id = @surveyId", new { surveyId }, tx)!;

    /// <summary>Runs the notification work behind a savepoint so that nothing
    /// it does -- or fails to do -- can take the caller's transaction with it.</summary>
    private async Task GuardedAsync(SqlTransaction tx, string what, long surveyId, Func<Task> work)
    {
        const string savepoint = "survey_notify";
        try
        {
            tx.Save(savepoint);
            await work();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not write the {What} notifications for survey {SurveyId}", what, surveyId);
            try { tx.Rollback(savepoint); }
            catch (Exception inner)
            {
                _log.LogError(inner, "Could not roll the notification work back to its savepoint");
            }
        }
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string What(Ctx x) =>
        "container " + (string.IsNullOrWhiteSpace(x.ContainerNo) ? x.ArrivalNo : x.ContainerNo)
        + (string.IsNullOrWhiteSpace(x.VendorName) ? "" : " (" + x.VendorName + ")");

    // ---- Events -------------------------------------------------------

    public Task RequestedAsync(SqlConnection c, SqlTransaction tx, long surveyId, string actor) =>
        GuardedAsync(tx, "requested", surveyId, async () =>
        {
            var x = await LoadAsync(c, tx, surveyId);
            if (x == null) return;
            var deciders = (await _recipients.WithPermissionAsync(c, tx, Perm.Surveys.Decide, x.Plant))
                .Where(r => !Same(r.Username, actor)).ToList();
            if (deciders.Count == 0) return;

            var surveyUrl = "/Surveys/Details/" + x.SurveyId;
            var title = $"Survey {x.SurveyNo} needs a decision";
            await _bell.AddAsync(c, tx, new AppNotification
            {
                EventCode = SurveyEvents.Requested, Title = title,
                Body = $"{x.RequestedBy} requested a survey for {What(x)}: {x.RequestReason}",
                EntityType = EntityTypes.Survey, EntityId = x.SurveyId,
                DedupeKey = $"survey:{x.SurveyId}:requested"
            }, deciders.Select(d => new NotificationTarget(d.UserId, surveyUrl)));

            var link = await AbsoluteAsync(surveyUrl);
            foreach (var d in deciders.Where(d => d.CanBeMailed))
                await _outbox.EnqueueAsync(c, tx, new OutboxMail
                {
                    EventCode = SurveyEvents.Requested, EntityType = EntityTypes.Survey, EntityId = x.SurveyId,
                    ToAddress = d.Email!, ToName = d.DisplayName ?? d.Username,
                    Subject = $"Survey {x.SurveyNo} needs a decision - {What(x)}",
                    BodyHtml = Mail(title,
                        "A survey was requested and is waiting for you to confirm or pass it. The QC process carries on in the meantime.",
                        Rows(x, ("Requested by", x.RequestedBy), ("Reason", x.RequestReason)),
                        "Open the survey", link),
                    DedupeKey = $"survey:{x.SurveyId}:requested:{d.Email!.Trim().ToLowerInvariant()}"
                });
        });

    public Task DecidedAsync(SqlConnection c, SqlTransaction tx, long surveyId, string actor) =>
        GuardedAsync(tx, "decided", surveyId, async () =>
        {
            var x = await LoadAsync(c, tx, surveyId);
            if (x == null || x.StatusCode == SurveyStatus.Open) return;
            var confirmed = x.StatusCode == SurveyStatus.Confirm;

            var targets = new List<NotificationTarget>();
            // Whoever asked. They may not be able to open the survey, so their
            // notification leads to the arrival, which they can.
            var requester = await _recipients.ByUsernameAsync(c, tx, x.RequestedBy);
            if (requester != null && !Same(requester.Username, actor))
                targets.Add(new NotificationTarget(requester.UserId, "/Arrivals/Details/" + x.ArrivalId));
            // The other deciders, so the same survey is not chased twice.
            foreach (var d in await _recipients.WithPermissionAsync(c, tx, Perm.Surveys.Decide, x.Plant))
                if (!Same(d.Username, actor) && targets.All(t => t.UserId != d.UserId))
                    targets.Add(new NotificationTarget(d.UserId, "/Surveys/Details/" + x.SurveyId));
            if (targets.Count == 0) return;

            await _bell.AddAsync(c, tx, new AppNotification
            {
                EventCode = confirmed ? SurveyEvents.Confirmed : SurveyEvents.Passed,
                Title = $"Survey {x.SurveyNo} was {(confirmed ? "confirmed" : "passed")}",
                Body = $"{x.DecidedBy} {(confirmed ? "confirmed" : "passed")} the survey for {What(x)}."
                     + (string.IsNullOrWhiteSpace(x.DecisionComments) ? "" : " " + x.DecisionComments!.Trim()),
                EntityType = EntityTypes.Survey, EntityId = x.SurveyId,
                DedupeKey = $"survey:{x.SurveyId}:decided"
            }, targets);
        });

    public Task ClaimRaisedAsync(SqlConnection c, SqlTransaction tx, long surveyId) =>
        GuardedAsync(tx, "claim raised", surveyId, async () =>
        {
            var x = await LoadAsync(c, tx, surveyId);
            if (x == null || !x.QoId.HasValue) return;
            var reviewers = await _recipients.WithPermissionAsync(c, tx, Perm.Claims.Approve, x.Plant);
            if (reviewers.Count == 0) return;

            var claimUrl = "/ClaimManagement/Details/" + x.QoId;
            var title = $"Claim notification request raised on {x.QoNo}";
            await _bell.AddAsync(c, tx, new AppNotification
            {
                EventCode = SurveyEvents.ClaimRaised, Title = title,
                Body = $"Raised automatically for {What(x)}: survey {x.SurveyNo} is confirmed and the quality order is finished.",
                EntityType = EntityTypes.Survey, EntityId = x.SurveyId,
                DedupeKey = $"survey:{x.SurveyId}:claimraised:{x.QoId}"
            }, reviewers.Select(r => new NotificationTarget(r.UserId, claimUrl)));

            var link = await AbsoluteAsync(claimUrl);
            foreach (var r in reviewers.Where(r => r.CanBeMailed))
                await _outbox.EnqueueAsync(c, tx, new OutboxMail
                {
                    EventCode = SurveyEvents.ClaimRaised, EntityType = EntityTypes.Survey, EntityId = x.SurveyId,
                    ToAddress = r.Email!, ToName = r.DisplayName ?? r.Username,
                    Subject = $"Claim notification request raised on {x.QoNo} - {What(x)}",
                    BodyHtml = Mail(title,
                        "The quality order is finished and its survey is confirmed, so it has entered claim review. Nothing has been approved or sent to the supplier.",
                        Rows(x, ("Confirmed by", x.DecidedBy ?? ""), ("Quality order", x.QoNo ?? "")),
                        "Open the claim", link),
                    DedupeKey = $"survey:{x.SurveyId}:claimraised:{x.QoId}:{r.Email!.Trim().ToLowerInvariant()}"
                });
        });

    // ---- The mail itself ------------------------------------------------

    private async Task<string> AbsoluteAsync(string relative)
    {
        var smtp = await _settings.GetSmtpConfigAsync();
        var baseUrl = (smtp.SiteUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) return "";
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
        return baseUrl + relative;
    }

    private static List<(string Label, string Value)> Rows(Ctx x, params (string Label, string Value)[] more)
    {
        var rows = new List<(string, string)>
        {
            ("Survey", x.SurveyNo),
            ("Arrival", x.ArrivalNo),
            ("Container", x.ContainerNo ?? ""),
            ("Supplier", x.VendorName ?? ""),
            ("Branch", x.Plant ?? ""),
        };
        rows.AddRange(more);
        return rows.Where(r => !string.IsNullOrWhiteSpace(r.Item2)).ToList();
    }

    // Inline colours and a table layout, like the finish notice: Outlook
    // ignores most of flex and grid and drops <style> blocks.
    private static string Mail(string title, string intro, List<(string Label, string Value)> rows,
        string buttonText, string link)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;color:#1f2933;max-width:640px\">");
        sb.Append($"<h2 style=\"font-size:18px;margin:0 0 8px\">{H(title)}</h2>");
        sb.Append($"<p style=\"margin:0 0 14px;font-size:14px\">{H(intro)}</p>");
        sb.Append("<table role=\"presentation\" style=\"border-collapse:collapse;font-size:14px;margin:0 0 16px\">");
        foreach (var (label, value) in rows)
            sb.Append("<tr>"
                + $"<td style=\"padding:4px 14px 4px 0;color:#7b8794;vertical-align:top;white-space:nowrap\">{H(label)}</td>"
                + $"<td style=\"padding:4px 0\">{H(value)}</td></tr>");
        sb.Append("</table>");
        if (!string.IsNullOrEmpty(link))
            sb.Append("<table role=\"presentation\" style=\"border-collapse:collapse\"><tr>"
                + "<td style=\"background:#0b5ed7;border-radius:6px\">"
                + $"<a href=\"{H(link)}\" style=\"display:block;padding:10px 18px;color:#fff;text-decoration:none;font-weight:600;font-size:14px\">{H(buttonText)} &rarr;</a>"
                + "</td></tr></table>");
        sb.Append("<p style=\"margin:16px 0 0;font-size:12px;color:#7b8794\">Internal and confidential. Please do not forward this message outside the company.</p>");
        sb.Append("</div>");
        return sb.ToString();
    }
}
