using System.Collections.Concurrent;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Stands in for SMTP in every test host (QmsAppFactory): nothing leaves the
/// machine, and each send is kept so a test can check what would have gone.
/// </summary>
public sealed class RecordingEmailService : IEmailService
{
    public static readonly RecordingEmailService Instance = new();

    public sealed record Sent(IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Subject, IReadOnlyList<string> Attachments);

    public ConcurrentQueue<Sent> Outbox { get; } = new();

    public Task<bool> SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        Outbox.Enqueue(new Sent(new[] { toEmail }, Array.Empty<string>(), subject, Array.Empty<string>()));
        return Task.FromResult(true);
    }

    public Task<(bool ok, string? error)> SendWithAttachmentsAsync(IEnumerable<string> to, IEnumerable<string> cc,
        string subject, string body, bool bodyIsHtml,
        IEnumerable<(byte[] bytes, string fileName, string mediaType)> attachments, CancellationToken ct = default)
    {
        Outbox.Enqueue(new Sent(to.ToList(), cc.ToList(), subject, attachments.Select(a => a.fileName).ToList()));
        return Task.FromResult<(bool, string?)>((true, null));
    }

    public Task<bool> SendToEmailGroupAsync(int groupId, string subject, string htmlBody, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(bool ok, string message)> SendTestEmailAsync(string toEmail, CancellationToken ct = default)
        => Task.FromResult((true, "recorded by the test host"));
}
