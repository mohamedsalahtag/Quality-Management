// Pack-provided alert orchestrator. Domain-agnostic: it delegates record
// scanning to IAlertDataSource and email send to IEmailService, and owns
// schedule logic + HTML body building.
//
// Hard rules from SPEC.md:
//   - Today-rule fires at most once per day per rule.
//   - A "Once" rule with OnceSent=true never fires again.
//   - Empty matches → never fire (don't email empty alerts).
//   - All SMTP errors caught and logged.
//   - All recipient sends fire-and-forget; never block caller.

using System.Net;
using System.Text;
using YourApp.Models;

namespace YourApp.Services;

public interface IAlertService
{
    Task<int>                 RunAlertAsync(AlertRule rule, bool forceIgnoreSchedule = false);
    Task<int>                 RunAllDueAsync();
    Task<List<AlertMatch>>    CollectMatchesAsync(AlertRule rule);
}

public class AlertService : IAlertService
{
    private readonly IDbService       _db;
    private readonly IEmailService    _email;
    private readonly IAlertDataSource _source;
    private readonly ILogger<AlertService> _logger;
    private readonly string           _appName;

    public AlertService(IDbService db, IEmailService email, IAlertDataSource source,
        IConfiguration cfg, ILogger<AlertService> logger)
    {
        _db = db; _email = email; _source = source; _logger = logger;
        _appName = cfg.GetValue<string>("AppSettings:AppDisplayName") ?? "Application";
    }

    public Task<List<AlertMatch>> CollectMatchesAsync(AlertRule rule)
        => _source.CollectMatchesAsync(rule);

    // -------------------- Schedule logic --------------------

    private static (bool send, string reason) ShouldFire(AlertRule rule, List<AlertMatch> matches, DateTime now)
    {
        var hasTodayMatch = matches.Any(m => m.DueDate?.Date == now.Date);
        var alreadyToday  = rule.LastSentDate.HasValue && rule.LastSentDate.Value.Date == now.Date;

        // Today-rule override — fires once per day regardless of schedule.
        if (hasTodayMatch && !alreadyToday)
            return (true, "today-rule");

        if (matches.Count == 0) return (false, "no-matches");

        switch (rule.Schedule)
        {
            case AlertSchedules.Once:
                return rule.OnceSent ? (false, "once-already-sent") : (true, "once");

            case AlertSchedules.Daily:
                return alreadyToday ? (false, "already-sent-today") : (true, "daily");

            case AlertSchedules.EveryN:
                if (rule.LastSentDate == null) return (true, $"every-{rule.ScheduleN}-first");
                var elapsed = (now.Date - rule.LastSentDate.Value.Date).Days;
                return elapsed >= rule.ScheduleN
                    ? (true,  $"every-{rule.ScheduleN} ({elapsed}d elapsed)")
                    : (false, $"every-{rule.ScheduleN} (only {elapsed}d elapsed)");

            default:
                return (false, "unknown-schedule");
        }
    }

    public async Task<int> RunAlertAsync(AlertRule rule, bool forceIgnoreSchedule = false)
    {
        if (!rule.IsActive && !forceIgnoreSchedule)
        {
            _logger.LogInformation("Alert '{Name}' inactive — skipping", rule.Name);
            return 0;
        }

        var matches = await _source.CollectMatchesAsync(rule);
        if (matches.Count == 0 && !forceIgnoreSchedule) return 0;

        if (!forceIgnoreSchedule)
        {
            var (send, reason) = ShouldFire(rule, matches, DateTime.Now);
            if (!send)
            {
                _logger.LogInformation("Alert '{Name}' not due ({Reason})", rule.Name, reason);
                return 0;
            }
            _logger.LogInformation("Alert '{Name}' firing ({Reason}) — {Count} matches",
                rule.Name, reason, matches.Count);
        }

        var html       = BuildAlertHtml(rule, matches);
        var subject    = BuildSubject(rule, matches);
        var dispatched = await _email.SendToEmailGroupAsync(rule.GroupId, subject, html);
        await _db.MarkAlertSentAsync(rule.AlertId, DateTime.Now);
        return dispatched;
    }

    public async Task<int> RunAllDueAsync()
    {
        var rules = await _db.GetAlertRulesAsync(activeOnly: true);
        int fired = 0;
        foreach (var r in rules)
        {
            var n = await RunAlertAsync(r);
            if (n > 0) fired++;
        }
        return fired;
    }

    // -------------------- Subject + body --------------------

    private static string BuildSubject(AlertRule rule, List<AlertMatch> matches)
    {
        var red    = matches.Count(m => m.Severity == AlertSeverities.Red);
        var yellow = matches.Count(m => m.Severity == AlertSeverities.Yellow);
        var parts  = new List<string>();
        if (red    > 0) parts.Add($"{red} expired");
        if (yellow > 0) parts.Add($"{yellow} expiring soon");
        var summary = parts.Count > 0 ? " — " + string.Join(", ", parts) : "";
        return $"{rule.Subject}{summary}";
    }

    private string BuildAlertHtml(AlertRule rule, List<AlertMatch> matches)
    {
        var todayMatches  = matches.Where(m => m.DueDate?.Date == DateTime.Today).ToList();
        var redMatches    = matches.Where(m => m.Severity == AlertSeverities.Red    && m.DueDate?.Date != DateTime.Today).ToList();
        var yellowMatches = matches.Where(m => m.Severity == AlertSeverities.Yellow && m.DueDate?.Date != DateTime.Today).ToList();

        // Header accent reflects severity at a glance.
        string accent =
            (redMatches.Count > 0 || todayMatches.Count > 0) ? "#dc2626" :
            (yellowMatches.Count > 0)                         ? "#f59e0b" :
                                                                "#2563eb";

        var sb = new StringBuilder();
        sb.Append(@"<!DOCTYPE html><html><body style='margin:0;padding:0;background:#f4f4f4;font-family:Segoe UI,Arial,sans-serif'>
<div style='max-width:760px;margin:32px auto;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 4px 16px rgba(15,23,42,0.10)'>
  <table cellspacing='0' cellpadding='0' style='width:100%;border-collapse:collapse'>
    <tr>
      <td style='background:#0f172a;padding:24px 32px;border-bottom:4px solid ");
        sb.Append(accent);
        sb.Append(@";color:#ffffff'>
        <div style='font-size:11px;color:#94a3b8;text-transform:uppercase;letter-spacing:1.6px;font-weight:600;margin-bottom:6px'>");
        sb.Append(WebUtility.HtmlEncode(_appName));
        sb.Append(@" &nbsp;&middot;&nbsp; Notification</div>
        <div style='font-size:20px;font-weight:600;color:#ffffff;line-height:1.3'>");
        sb.Append(WebUtility.HtmlEncode(rule.Name));
        sb.Append(@"</div>
      </td>
    </tr>
  </table>
  <div style='padding:28px 32px'>
    <p style='color:#374151;margin:0 0 18px;font-size:14px'>
        The following records need attention as of <b>");
        sb.Append(DateTime.Now.ToString("dd MMM yyyy"));
        sb.Append(@"</b>.</p>");

        if (todayMatches.Any())
        {
            sb.Append("<div style='background:#fef2f2;border:2px solid #dc2626;border-radius:8px;padding:14px;margin-bottom:20px'>");
            sb.Append("<div style='color:#7f1d1d;font-weight:700;margin-bottom:8px;font-size:14px'>");
            sb.Append("<span style='display:inline-block;width:10px;height:10px;border-radius:50%;background:#dc2626;margin-right:8px'></span>");
            sb.Append($"DUE TODAY — {todayMatches.Count} record(s)</div>");
            sb.Append(RenderTable(todayMatches, isToday: true));
            sb.Append("</div>");
        }
        if (redMatches.Any())    sb.Append(RenderSection("Expired",       "#dc2626", redMatches));
        if (yellowMatches.Any()) sb.Append(RenderSection("Expiring soon", "#d97706", yellowMatches));

        sb.Append($"<p style='color:#94a3b8;font-size:12px;margin-top:24px'>Total: {matches.Count} record(s) • Sent at {DateTime.Now:dd MMM yyyy HH:mm}</p>");
        sb.Append("</div><div style='background:#f8fafc;padding:14px 32px;font-size:12px;color:#94a3b8;text-align:center'>");
        sb.Append($"Automated notification from {WebUtility.HtmlEncode(_appName)}. Do not reply.");
        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }

    private static string RenderSection(string title, string accent, List<AlertMatch> rows)
    {
        var sb = new StringBuilder();
        sb.Append($"<h3 style='color:#1e293b;font-size:15px;margin:24px 0 10px;border-bottom:2px solid {accent};padding-bottom:6px'>");
        sb.Append($"<span style='display:inline-block;width:10px;height:10px;border-radius:50%;background:{accent};margin-right:8px;vertical-align:middle'></span>");
        sb.Append($"{WebUtility.HtmlEncode(title)} — {rows.Count}</h3>");
        sb.Append(RenderTable(rows));
        return sb.ToString();
    }

    private static string RenderTable(List<AlertMatch> rows, bool isToday = false)
    {
        var sb = new StringBuilder();
        sb.Append(@"<table style='width:100%;border-collapse:collapse;font-size:13px'>
            <thead><tr style='background:#f1f5f9;color:#475569;text-align:left'>
                <th style='padding:8px;width:18px'></th>
                <th style='padding:8px'>ID</th>
                <th style='padding:8px'>Record</th>
                <th style='padding:8px'>Type</th>
                <th style='padding:8px'>Aspect</th>
                <th style='padding:8px;text-align:right'>Due</th>
                <th style='padding:8px;text-align:right'>Days</th>
                <th style='padding:8px'>Owner</th>
            </tr></thead><tbody>");
        for (var i = 0; i < rows.Count; i++)
        {
            var m   = rows[i];
            var bg  = i % 2 == 0 ? "#ffffff" : "#f8fafc";
            var dot = m.Severity == AlertSeverities.Red ? "#dc2626" : "#d97706";
            var pretty = m.DueDate?.ToString("dd MMM yyyy") ?? m.DueRaw;
            var days = isToday ? "TODAY"
                : (m.DaysFromToday < 0 ? $"{Math.Abs(m.DaysFromToday)}d ago" : $"{m.DaysFromToday}d");
            sb.Append($"<tr style='background:{bg};border-bottom:1px solid #e2e8f0'>");
            sb.Append($"<td style='padding:8px'><span style='display:inline-block;width:10px;height:10px;border-radius:50%;background:{dot}'></span></td>");
            sb.Append($"<td style='padding:8px;font-family:Consolas,monospace;font-weight:600'>{WebUtility.HtmlEncode(m.Identifier)}</td>");
            sb.Append($"<td style='padding:8px'>{WebUtility.HtmlEncode(m.Label)}</td>");
            sb.Append($"<td style='padding:8px;color:#64748b'>{WebUtility.HtmlEncode(m.PrimaryType)}</td>");
            sb.Append($"<td style='padding:8px'>{WebUtility.HtmlEncode(m.SecondaryType)}</td>");
            sb.Append($"<td style='padding:8px;text-align:right'>{WebUtility.HtmlEncode(pretty)}</td>");
            sb.Append($"<td style='padding:8px;text-align:right;color:{dot};font-weight:600'>{WebUtility.HtmlEncode(days)}</td>");
            sb.Append($"<td style='padding:8px;color:#64748b'>{WebUtility.HtmlEncode(m.Owner)}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
        return sb.ToString();
    }
}
