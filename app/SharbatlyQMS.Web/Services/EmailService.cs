using Dapper;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Data.SqlClient;
using MimeKit;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Adapted from email-notification-pack/reference/EmailService.cs.
/// Differences vs. the source pack:
///  * No per-group SMTP override yet (single global SMTP profile from
///    SiteConfiguration). Per-group override can be added later by reading
///    GroupMailConfig before falling back to the global keys.
///  * No HTML template builder shipped here -- callers (alert engine, admin
///    test page) supply full HTML. Templates can be added when the alert
///    engine needs them.
/// </summary>
public class EmailService : IEmailService
{
    private static readonly string[] SmtpKeys =
    {
        "SmtpHost", "SmtpPort", "SmtpUser", "SmtpPassword",
        "SmtpFromEmail", "SmtpFromName", "SmtpEnableSsl", "SiteName"
    };

    private readonly IDbService _db;
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IDbService db, IConfiguration config, ILogger<EmailService> logger)
    {
        _db = db; _config = config; _logger = logger;
    }

    public async Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail)) return false;

        var (ok, smtp, msg) = await BuildMessageAsync(new[] { (toEmail, "To") }, subject, htmlBody);
        if (!ok)
        {
            _logger.LogWarning("Email skipped: {Reason}", msg);
            return false;
        }
        return await SendInternalAsync(smtp!, ct);
    }

    public async Task<bool> SendToEmailGroupAsync(int groupId, string subject, string htmlBody, CancellationToken ct = default)
    {
        var addresses = await GetGroupAddressesAsync(groupId);
        if (addresses.Count == 0)
        {
            _logger.LogWarning("Email group {GroupId} has no addresses; skipping send", groupId);
            return false;
        }

        var (ok, smtp, msg) = await BuildMessageAsync(addresses, subject, htmlBody);
        if (!ok)
        {
            _logger.LogWarning("Email skipped: {Reason}", msg);
            return false;
        }
        return await SendInternalAsync(smtp!, ct);
    }

    public async Task<(bool ok, string? error)> SendWithAttachmentsAsync(
        IEnumerable<string> to,
        IEnumerable<string> cc,
        string subject,
        string body,
        bool bodyIsHtml,
        IEnumerable<(byte[] bytes, string fileName, string mediaType)> attachments,
        CancellationToken ct = default)
    {
        var toList = (to ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim()).ToList();
        var ccList = (cc ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim()).ToList();
        if (toList.Count == 0 && ccList.Count == 0)
            return (false, "At least one recipient is required.");

        var cfg = await _db.GetConfigManyAsync(SmtpKeys);
        var host      = cfg.GetValueOrDefault("SmtpHost");
        var fromEmail = cfg.GetValueOrDefault("SmtpFromEmail");
        var fromName  = cfg.GetValueOrDefault("SmtpFromName") ?? cfg.GetValueOrDefault("SiteName") ?? "Sharbatly QMS";
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromEmail))
            return (false, "SMTP host or sender address not configured.");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(fromName, fromEmail));
        try
        {
            foreach (var addr in toList) msg.To.Add(MailboxAddress.Parse(addr));
            foreach (var addr in ccList) msg.Cc.Add(MailboxAddress.Parse(addr));
        }
        catch (Exception ex)
        {
            return (false, "Invalid email address: " + ex.Message);
        }

        msg.Subject = subject ?? "";
        var bb = new BodyBuilder();
        if (bodyIsHtml) bb.HtmlBody = body; else bb.TextBody = body;
        foreach (var a in attachments ?? Array.Empty<(byte[], string, string)>())
        {
            if (a.bytes == null || a.bytes.Length == 0) continue;
            var ct2 = MimeKit.ContentType.Parse(string.IsNullOrWhiteSpace(a.mediaType) ? "application/octet-stream" : a.mediaType);
            bb.Attachments.Add(a.fileName ?? "attachment", a.bytes, ct2);
        }
        msg.Body = bb.ToMessageBody();

        var ok = await SendInternalAsync(msg, ct);
        return ok ? (true, null) : (false, "SMTP send failed; see application log.");
    }

    public async Task<(bool ok, string message)> SendTestEmailAsync(string toEmail, CancellationToken ct = default)
    {
        try
        {
            var sent = await SendAsync(toEmail,
                "Sharbatly QMS test email",
                "<p>This is a test message from the Sharbatly Quality Management System.</p>" +
                "<p>If you received this, your SMTP configuration is working.</p>",
                ct);
            return sent
                ? (true, $"Test email sent to {toEmail}.")
                : (false, "Send failed -- check SMTP configuration. See application log for details.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test email failed");
            return (false, $"Send failed: {ex.Message}");
        }
    }

    // ---- internals --------------------------------------------------------

    private async Task<(bool ok, MimeMessage? msg, string reason)> BuildMessageAsync(
        IReadOnlyList<(string email, string recipient)> recipients, string subject, string htmlBody)
    {
        var cfg = await _db.GetConfigManyAsync(SmtpKeys);
        var host       = cfg.GetValueOrDefault("SmtpHost");
        var fromEmail  = cfg.GetValueOrDefault("SmtpFromEmail");
        var fromName   = cfg.GetValueOrDefault("SmtpFromName") ?? cfg.GetValueOrDefault("SiteName") ?? "Sharbatly QMS";

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromEmail))
            return (false, null, "SMTP host or sender address not configured");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(fromName, fromEmail));
        foreach (var (addr, kind) in recipients)
        {
            if (string.IsNullOrWhiteSpace(addr)) continue;
            var box = MailboxAddress.Parse(addr);
            if (string.Equals(kind, "CC", StringComparison.OrdinalIgnoreCase))
                msg.Cc.Add(box);
            else
                msg.To.Add(box);
        }
        if (msg.To.Count == 0 && msg.Cc.Count == 0)
            return (false, null, "No valid recipients");

        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();
        return (true, msg, "");
    }

    private async Task<bool> SendInternalAsync(MimeMessage msg, CancellationToken ct)
    {
        var cfg = await _db.GetConfigManyAsync(SmtpKeys);
        var host = cfg.GetValueOrDefault("SmtpHost") ?? "";
        var port = int.TryParse(cfg.GetValueOrDefault("SmtpPort"), out var p) ? p : 587;
        var user = cfg.GetValueOrDefault("SmtpUser") ?? "";
        var pass = cfg.GetValueOrDefault("SmtpPassword") ?? "";
        var ssl  = string.Equals(cfg.GetValueOrDefault("SmtpEnableSsl"), "true", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(host, port,
                ssl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
            if (!string.IsNullOrEmpty(user))
                await smtp.AuthenticateAsync(user, pass, ct);
            await smtp.SendAsync(msg, ct);
            await smtp.DisconnectAsync(true, ct);
            _logger.LogInformation("Email sent to {To}: {Subject}",
                string.Join(",", msg.To.Mailboxes.Select(m => m.Address)), msg.Subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP send failed (host={Host}, port={Port})", host, port);
            return false;
        }
    }

    private async Task<List<(string email, string recipient)>> GetGroupAddressesAsync(int groupId)
    {
        var cs = _config.GetConnectionString("Default")!;
        using var c = new SqlConnection(cs);
        var rows = await c.QueryAsync<(string EmailAddress, string Recipient)>(
            "SELECT EmailAddress, Recipient FROM EmailGroupAddresses WHERE GroupId = @groupId",
            new { groupId });
        return rows.Select(r => (r.EmailAddress, r.Recipient)).ToList();
    }
}
