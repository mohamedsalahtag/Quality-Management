// Snippet for your project's IEmailService — the To/CC fan-out method that
// the alert engine calls. If you already installed email-notification-pack,
// just add this method to its EmailService class. If you're rolling your
// own email plumbing, copy the whole pattern.

using MailKit.Net.Smtp;
using MimeKit;
using YourApp.Models;

namespace YourApp.Services;

public partial class EmailService     // partial — extend your existing class
{
    public async Task<int> SendToEmailGroupAsync(int groupId, string subject, string htmlBody)
    {
        var addresses = await _db.GetEmailGroupAddressesAsync(groupId);

        bool Valid(string e) => !string.IsNullOrWhiteSpace(e)
            && !e.Contains("placeholder", StringComparison.OrdinalIgnoreCase);

        var toList = addresses
            .Where(a => string.Equals(a.Recipient, "To", StringComparison.OrdinalIgnoreCase))
            .Select(a => (a.EmailAddress ?? "").Trim()).Where(Valid).Distinct().ToList();
        var ccList = addresses
            .Where(a => string.Equals(a.Recipient, "CC", StringComparison.OrdinalIgnoreCase))
            .Select(a => (a.EmailAddress ?? "").Trim()).Where(Valid)
            .Distinct().Where(e => !toList.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();

        // SMTP requires at least one To. Promote first CC if needed.
        if (toList.Count == 0 && ccList.Count > 0)
        {
            toList.Add(ccList[0]); ccList.RemoveAt(0);
        }

        var total = toList.Count + ccList.Count;
        if (total == 0) return 0;

        // Single MimeMessage with multiple To/CC entries — recipients see each
        // other in the headers (true CC behavior).
        _ = Task.Run(() => SendMultiAsync(toList, ccList, subject, htmlBody, groupId));
        return total;
    }

    private async Task SendMultiAsync(List<string> toList, List<string> ccList,
        string subject, string htmlBody, int? groupId)
    {
        if (toList.Count == 0) return;
        try
        {
            var (host, port, user, pass, ssl, from, fromName, _) = await GetSmtpAsync(groupId);
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                _logger.LogWarning("Email-group SMTP not configured (group={G})", groupId);
                return;
            }
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(fromName, from));
            foreach (var t in toList) msg.To.Add(MailboxAddress.Parse(t));
            foreach (var c in ccList) msg.Cc.Add(MailboxAddress.Parse(c));
            msg.Subject = subject;
            msg.Body    = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(host, port,
                ssl ? MailKit.Security.SecureSocketOptions.StartTls
                    : MailKit.Security.SecureSocketOptions.None);
            await client.AuthenticateAsync(user, pass);
            await client.SendAsync(msg);
            await client.DisconnectAsync(true);
            _logger.LogInformation("Email-group sent: {To} To, {Cc} CC: {Subj}",
                toList.Count, ccList.Count, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email-group send failed (To={ToCount}, CC={CcCount})",
                toList.Count, ccList.Count);
        }
    }
}
