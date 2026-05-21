namespace SharbatlyQMS.Web.Services;

public interface IEmailService
{
    /// <summary>
    /// Sends a single transactional email using the global SMTP configuration
    /// stored in <c>SiteConfiguration</c>. Returns true on success, false if SMTP
    /// is not configured or the send failed (the failure is logged).
    /// </summary>
    Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);

    /// <summary>
    /// Send an email with CC and one or more in-memory attachments. Used by
    /// the Quality Order "Send report to supplier" flow. <paramref name="bodyIsHtml"/>
    /// switches between plain-text and HTML body, since the operator-typed
    /// body in the dialog is plain text.
    /// </summary>
    Task<(bool ok, string? error)> SendWithAttachmentsAsync(
        IEnumerable<string> to,
        IEnumerable<string> cc,
        string subject,
        string body,
        bool bodyIsHtml,
        IEnumerable<(byte[] bytes, string fileName, string mediaType)> attachments,
        CancellationToken ct = default);

    /// <summary>
    /// Fans out one email to every address registered in <c>EmailGroupAddresses</c>
    /// for the given group. Addresses with <c>Recipient='To'</c> become To headers
    /// and <c>Recipient='CC'</c> become CC. Used by the alert engine.
    /// </summary>
    Task<bool> SendToEmailGroupAsync(int groupId, string subject, string htmlBody, CancellationToken ct = default);

    /// <summary>
    /// Convenience: send a "test email" using whatever SMTP settings are currently
    /// stored, so admins can verify configuration end-to-end.
    /// </summary>
    Task<(bool ok, string message)> SendTestEmailAsync(string toEmail, CancellationToken ct = default);
}
