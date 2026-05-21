// ============================================================
// EmailService - Reference Implementation (.NET 9 + MailKit)
// ============================================================
// This is the actual shipping code from c:\HelpDesk. It speaks the contract
// described in SPEC.md. When porting to another stack, preserve:
//   - The IEmailService method shapes
//   - The fire-and-forget rule (never block the request)
//   - The SMTP resolution algorithm (group override -> global fallback)
//   - The HTML template structure and pill colors
//   - The reply-thread email (full conversation, NEW reply highlighted)
//
// External assumptions:
//   - IDbService exposes GetAllConfigAsync()  -> Dictionary<string,string>
//                        GetGroupMailConfigAsync(int groupId) -> GroupMailConfig?
//   - Models: Ticket, TicketReply, GroupMailConfig
//   - NuGet: MailKit, MimeKit
// ============================================================

using MailKit.Net.Smtp;
using MimeKit;

namespace HelpDesk.Services;

public interface IEmailService
{
    Task SendTicketCreatedAsync(Ticket ticket, string requesterEmail);
    Task SendTicketPickedUpAsync(Ticket ticket, string techName, string requesterEmail);
    Task SendTicketReplyAsync(Ticket ticket, TicketReply newReply, List<TicketReply> allReplies, string requesterEmail);
    Task SendTicketStatusChangedAsync(Ticket ticket, string newStatus, string requesterEmail);
    Task SendToGeneralInboxAsync(Ticket ticket);
    Task SendNewTicketToTechniciansAsync(Ticket ticket, List<string> techEmails, string siteUrl);
}

public class EmailService : IEmailService
{
    private readonly IDbService _db;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IDbService db, ILogger<EmailService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // -------------------------------------------------------------------
    // SMTP resolution: group override -> global fallback. See SPEC.md §3.
    // -------------------------------------------------------------------
    private async Task<(string host, int port, string user, string pass, bool ssl, string from, string fromName, string siteName)>
        GetSmtpConfigForGroupAsync(int? groupId = null)
    {
        var cfg      = await _db.GetAllConfigAsync();
        var siteName = cfg.GetValueOrDefault("SiteName", "IT HelpDesk");

        if (groupId.HasValue)
        {
            var grpCfg = await _db.GetGroupMailConfigAsync(groupId.Value);
            if (grpCfg != null && grpCfg.IsEnabled
                && !string.IsNullOrEmpty(grpCfg.SmtpHost)
                && !string.IsNullOrEmpty(grpCfg.SmtpUser)
                && !string.IsNullOrEmpty(grpCfg.SmtpPassword))
            {
                var fromName = string.IsNullOrEmpty(grpCfg.SmtpFromName) ? siteName : grpCfg.SmtpFromName;
                var from     = string.IsNullOrEmpty(grpCfg.SmtpFromEmail) ? grpCfg.SmtpUser : grpCfg.SmtpFromEmail;
                return (grpCfg.SmtpHost, grpCfg.SmtpPort, grpCfg.SmtpUser, grpCfg.SmtpPassword,
                        grpCfg.SmtpEnableSsl, from, fromName, siteName);
            }
        }

        var globalFrom = cfg.GetValueOrDefault("SmtpFromEmail", "");
        var globalUser = cfg.GetValueOrDefault("SmtpUser", "");
        return (
            cfg.GetValueOrDefault("SmtpHost", ""),
            int.TryParse(cfg.GetValueOrDefault("SmtpPort", "587"), out var p) ? p : 587,
            globalUser,
            cfg.GetValueOrDefault("SmtpPassword", ""),
            cfg.GetValueOrDefault("SmtpEnableSsl", "true") == "true",
            string.IsNullOrEmpty(globalFrom) ? globalUser : globalFrom,
            siteName,
            siteName
        );
    }

    private async Task SendAsync(string to, string subject, string htmlBody, int? groupId = null)
    {
        if (string.IsNullOrEmpty(to)) return;
        try
        {
            var (host, port, user, pass, ssl, from, fromName, _) =
                await GetSmtpConfigForGroupAsync(groupId);

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                _logger.LogWarning("Email skipped - SMTP not configured (group={GroupId})", groupId);
                return;
            }

            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(fromName, from));
            msg.To.Add(MailboxAddress.Parse(to));
            msg.Subject = subject;
            msg.Body    = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(host, port,
                ssl ? MailKit.Security.SecureSocketOptions.StartTls
                    : MailKit.Security.SecureSocketOptions.None);
            await client.AuthenticateAsync(user, pass);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            _logger.LogInformation("Email sent to {To}: {Subject} (group={GroupId})", to, subject, groupId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To} (group={GroupId})", to, groupId);
        }
    }

    // -------------------------------------------------------------------
    // Shared HTML shell. See SPEC.md §4 for the design.
    // -------------------------------------------------------------------
    private string Template(Ticket ticket, string headline, string bodyHtml, string? extraInfo = null)
    {
        var statusColor = ticket.Status switch
        {
            "New" => "#6c757d", "Open" => "#fd7e14",
            "InProgress" => "#0d6efd", "Assigned" => "#0d6efd",
            "Escalated" => "#dc3545", "Closed" => "#198754",
            "Cancelled" => "#6c757d", _ => "#6c757d"
        };
        var priorityColor = ticket.Priority switch
        {
            "Critical" => "#dc3545", "High" => "#fd7e14",
            "Medium" => "#0dcaf0", _ => "#6c757d"
        };

        return $@"<!DOCTYPE html><html><body style='margin:0;padding:0;background:#f4f4f4;font-family:Segoe UI,Arial,sans-serif'>
<div style='max-width:620px;margin:32px auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.08)'>
  <div style='background:#1a56db;padding:24px 32px'>
    <div style='color:#fff;font-size:22px;font-weight:600'>IT HelpDesk Portal</div>
    <div style='color:#93c5fd;font-size:14px;margin-top:4px'>Ticket #{ticket.TicketNumber}</div>
  </div>
  <div style='padding:32px'>
    <h2 style='margin:0 0 16px;color:#1e293b;font-size:18px'>{headline}</h2>
    {bodyHtml}
    {(extraInfo != null ? $"<div style='background:#f0f7ff;border-left:4px solid #1a56db;padding:16px;margin:20px 0;border-radius:0 6px 6px 0'>{extraInfo}</div>" : "")}
    <hr style='border:none;border-top:1px solid #e2e8f0;margin:24px 0'>
    <table style='width:100%;font-size:13px;border-collapse:collapse'>
      <tr><td style='padding:6px 0;color:#64748b;width:120px'>Ticket #</td><td style='padding:6px 0;font-weight:600'>{ticket.TicketNumber}</td></tr>
      <tr><td style='padding:6px 0;color:#64748b'>Subject</td><td style='padding:6px 0'>{ticket.Subject}</td></tr>
      <tr><td style='padding:6px 0;color:#64748b'>Category</td><td style='padding:6px 0'>{ticket.CategoryName}</td></tr>
      <tr><td style='padding:6px 0;color:#64748b'>Priority</td><td style='padding:6px 0'><span style='background:{priorityColor};color:#fff;padding:2px 10px;border-radius:20px;font-size:12px'>{ticket.Priority}</span></td></tr>
      <tr><td style='padding:6px 0;color:#64748b'>Status</td><td style='padding:6px 0'><span style='background:{statusColor};color:#fff;padding:2px 10px;border-radius:20px;font-size:12px'>{ticket.Status}</span></td></tr>
      <tr><td style='padding:6px 0;color:#64748b'>Submitted</td><td style='padding:6px 0'>{ticket.SubmittedAt:dd MMM yyyy HH:mm}</td></tr>
    </table>
  </div>
  <div style='background:#f8fafc;padding:14px 32px;font-size:12px;color:#94a3b8;text-align:center'>
    This is an automated notification from IT HelpDesk. Please do not reply to this email.
  </div>
</div></body></html>";
    }

    // -------------------------------------------------------------------
    // Trigger #1 - confirmation to the requester
    // -------------------------------------------------------------------
    public async Task SendTicketCreatedAsync(Ticket ticket, string requesterEmail)
    {
        var body = $@"<p style='color:#374151'>Hello <b>{ticket.RequesterName}</b>,</p>
            <p>Your support ticket has been successfully submitted. Our IT team will review it and get back to you shortly.</p>
            <p style='color:#64748b;font-size:14px'>You will receive an email notification when a technician picks up your ticket.</p>";
        await SendAsync(requesterEmail,
            $"[HelpDesk] Ticket #{ticket.TicketNumber} Submitted - {ticket.Subject}",
            Template(ticket, "Ticket Submitted Successfully", body),
            ticket.GroupId);
    }

    // -------------------------------------------------------------------
    // Trigger #3 - tell the requester who picked it up
    // -------------------------------------------------------------------
    public async Task SendTicketPickedUpAsync(Ticket ticket, string techName, string requesterEmail)
    {
        var body = $@"<p style='color:#374151'>Hello <b>{ticket.RequesterName}</b>,</p>
            <p>Good news! Your ticket is now being handled by our IT team.</p>";
        var extra = $"<b>Assigned Technician:</b> {techName}<br><span style='color:#64748b;font-size:13px'>Your ticket is now In Progress. You will be notified when there are updates or when it is resolved.</span>";
        await SendAsync(requesterEmail,
            $"[HelpDesk] Ticket #{ticket.TicketNumber} Picked Up - {ticket.Subject}",
            Template(ticket, "Your Ticket Has Been Picked Up", body, extra),
            ticket.GroupId);
    }

    // -------------------------------------------------------------------
    // Trigger #4 - reply added (full conversation thread)
    // SPEC.md §5 describes the per-card formatting.
    // -------------------------------------------------------------------
    public async Task SendTicketReplyAsync(Ticket ticket, TicketReply newReply, List<TicketReply> allReplies, string requesterEmail)
    {
        var historyHtml = new System.Text.StringBuilder();
        var publicReplies = allReplies.Where(r => !r.IsInternal).OrderBy(r => r.CreatedAt).ToList();

        foreach (var r in publicReplies)
        {
            var isNew    = r.ReplyId == newReply.ReplyId;
            var isStaff  = r.AuthorRole != "Requester";
            var bgColor  = isNew ? "#eff6ff" : (isStaff ? "#f8fafc" : "#f0fdf4");
            var border   = isNew ? "2px solid #1a56db" : "1px solid #e2e8f0";
            var label    = isStaff ? $"{r.AuthorName} - IT Support" : $"{r.AuthorName}";
            var timeStr  = r.CreatedAt != default ? r.CreatedAt.ToString("dd MMM yyyy HH:mm") : "";
            var newBadge = isNew ? "<span style='background:#1a56db;color:#fff;font-size:11px;padding:2px 8px;border-radius:10px;margin-left:8px'>NEW</span>" : "";
            historyHtml.Append($@"
            <div style='border:{border};border-radius:8px;padding:16px;margin-bottom:12px;background:{bgColor}'>
              <div style='display:flex;justify-content:space-between;margin-bottom:10px;align-items:center'>
                <span style='font-weight:600;color:#1e293b;font-size:13px'>{label}{newBadge}</span>
                <span style='color:#94a3b8;font-size:12px'>{timeStr}</span>
              </div>
              <div style='color:#374151;line-height:1.7;font-size:14px;white-space:pre-wrap'>{System.Net.WebUtility.HtmlEncode(r.Message)}</div>
            </div>");
        }

        var body = $@"<p style='color:#374151'>Hello <b>{ticket.RequesterName}</b>,</p>
            <p>A new reply has been added to your ticket by <b>{newReply.AuthorName}</b> from IT Support.</p>
            <h3 style='color:#1e293b;font-size:15px;margin:24px 0 12px;border-bottom:2px solid #e2e8f0;padding-bottom:8px'>
              Conversation History
            </h3>
            {historyHtml}";

        await SendAsync(requesterEmail,
            $"[HelpDesk] New Reply on Ticket #{ticket.TicketNumber} - {ticket.Subject}",
            Template(ticket, "New Reply on Your Ticket", body),
            ticket.GroupId);
    }

    // -------------------------------------------------------------------
    // Trigger #5 - status change (Closed / Cancelled / Escalated / other)
    // -------------------------------------------------------------------
    public async Task SendTicketStatusChangedAsync(Ticket ticket, string newStatus, string requesterEmail)
    {
        var (headline, bodyText) = newStatus switch
        {
            "Closed"    => ("Your Ticket Has Been Closed",
                            $"<p style='color:#374151'>Hello <b>{ticket.RequesterName}</b>,</p><p>Your support ticket has been <b style='color:#198754'>resolved and closed</b>. We hope your issue has been fully addressed.</p><p style='color:#64748b;font-size:14px'>If you experience the same issue again, please submit a new ticket.</p>"),
            "Cancelled" => ("Your Ticket Has Been Cancelled",
                            $"<p style='color:#374151'>Hello <b>{ticket.RequesterName}</b>,</p><p>Your ticket has been cancelled.</p>"),
            "Escalated" => ("Your Ticket Has Been Escalated",
                            $"<p style='color:#374151'>Hello <b>{ticket.RequesterName}</b>,</p><p>Your ticket has been escalated for priority handling.</p>"),
            _           => ($"Ticket Status Updated: {newStatus}",
                            $"<p style='color:#374151'>Hello <b>{ticket.RequesterName}</b>,</p><p>Your ticket status has been updated to <b>{newStatus}</b>.</p>")
        };
        await SendAsync(requesterEmail,
            $"[HelpDesk] Ticket #{ticket.TicketNumber} {newStatus} - {ticket.Subject}",
            Template(ticket, headline, bodyText),
            ticket.GroupId);
    }

    // -------------------------------------------------------------------
    // Trigger #6 - copy every new ticket to the General Ticket Inbox
    // -------------------------------------------------------------------
    public async Task SendToGeneralInboxAsync(Ticket ticket)
    {
        var cfg = await _db.GetAllConfigAsync();
        var generalInbox = cfg.GetValueOrDefault("GeneralTicketEmail", "");
        if (string.IsNullOrEmpty(generalInbox)) return;

        var body = $@"<p style='color:#374151'>A new support ticket has been submitted.</p>
            <div style='background:#f8fafc;border:1px solid #e2e8f0;border-radius:6px;padding:16px;margin:16px 0'>
                <div style='margin-bottom:8px'><b>Requester:</b> {ticket.RequesterName}</div>
                <div style='margin-bottom:8px'><b>Department:</b> {ticket.RequesterDept}</div>
                <div style='margin-bottom:8px'><b>Subject:</b> {ticket.Subject}</div>
                <div style='margin-top:12px;border-top:1px solid #e2e8f0;padding-top:12px;color:#374151;line-height:1.6'>{ticket.Details}</div>
            </div>";
        await SendAsync(generalInbox,
            $"[HelpDesk] New Ticket #{ticket.TicketNumber} - {ticket.Subject} [{ticket.Priority}]",
            Template(ticket, "New Support Ticket Submitted", body),
            ticket.GroupId);
    }

    // -------------------------------------------------------------------
    // Trigger #2 - fan-out to every technician in the group + SiteAdmins
    // Each recipient gets its own outbound message via Task.Run, so a slow
    // SMTP server doesn't block the whole list.
    // -------------------------------------------------------------------
    public async Task SendNewTicketToTechniciansAsync(Ticket ticket, List<string> techEmails, string siteUrl)
    {
        if (!techEmails.Any()) return;

        var detailUrl = $"{siteUrl.TrimEnd('/')}/Ticket/Detail/{ticket.TicketId}";
        var subject   = $"[HelpDesk] New Request #{ticket.TicketNumber} - {ticket.Subject}";

        var html = $@"<!DOCTYPE html><html><body style='margin:0;padding:0;background:#f4f4f4;font-family:Segoe UI,Arial,sans-serif'>
<div style='max-width:620px;margin:32px auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.08)'>
  <div style='background:#1a56db;padding:24px 32px'>
    <div style='color:#fff;font-size:22px;font-weight:600'>IT HelpDesk Portal</div>
    <div style='color:#93c5fd;font-size:14px;margin-top:4px'>New Request - Action Required</div>
  </div>
  <div style='padding:32px'>
    <p style='color:#374151;margin:0 0 20px'>A new support request has been submitted and is awaiting pickup.</p>
    <table style='width:100%;border-collapse:collapse;font-size:14px'>
      <tr style='border-bottom:1px solid #e2e8f0'>
        <td style='padding:10px 0;color:#64748b;width:140px;font-weight:600'>Request ID</td>
        <td style='padding:10px 0;font-weight:700;color:#1a56db'>#{ticket.TicketNumber}</td>
      </tr>
      <tr style='border-bottom:1px solid #e2e8f0'>
        <td style='padding:10px 0;color:#64748b;font-weight:600'>Submitted By</td>
        <td style='padding:10px 0'>{ticket.RequesterName}</td>
      </tr>
      <tr style='border-bottom:1px solid #e2e8f0'>
        <td style='padding:10px 0;color:#64748b;font-weight:600'>Title</td>
        <td style='padding:10px 0;font-weight:600'>{ticket.Subject}</td>
      </tr>
      <tr style='border-bottom:1px solid #e2e8f0'>
        <td style='padding:10px 0;color:#64748b;font-weight:600'>Category</td>
        <td style='padding:10px 0'>{ticket.CategoryName}</td>
      </tr>
      <tr style='border-bottom:1px solid #e2e8f0'>
        <td style='padding:10px 0;color:#64748b;font-weight:600'>Priority</td>
        <td style='padding:10px 0'>
          <span style='background:{(ticket.Priority == "Critical" ? "#dc3545" : ticket.Priority == "High" ? "#fd7e14" : ticket.Priority == "Medium" ? "#0dcaf0" : "#6c757d")};color:#fff;padding:3px 12px;border-radius:20px;font-size:12px'>{ticket.Priority}</span>
        </td>
      </tr>
      <tr>
        <td style='padding:10px 0;color:#64748b;font-weight:600;vertical-align:top'>Description</td>
        <td style='padding:10px 0;white-space:pre-wrap;line-height:1.6'>{ticket.Details}</td>
      </tr>
    </table>
    <div style='margin-top:28px;text-align:center'>
      <a href='{detailUrl}' style='background:#1a56db;color:#fff;padding:14px 36px;border-radius:8px;text-decoration:none;font-weight:600;font-size:15px;display:inline-block'>
        Click for Details
      </a>
    </div>
  </div>
  <div style='background:#f8fafc;padding:14px 32px;font-size:12px;color:#94a3b8;text-align:center'>
    Automated notification from IT HelpDesk. Do not reply to this email.
  </div>
</div></body></html>";

        foreach (var email in techEmails.Where(e => !string.IsNullOrEmpty(e)))
        {
            var emailCopy = email;
            _ = Task.Run(async () =>
            {
                try { await SendAsync(emailCopy, subject, html, ticket.GroupId); }
                catch (Exception ex) { _logger.LogError(ex, "Tech notification failed to {Email}", emailCopy); }
            });
        }
    }
}
