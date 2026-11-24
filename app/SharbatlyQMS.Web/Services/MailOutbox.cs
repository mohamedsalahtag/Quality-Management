using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services;

/// <summary>One e-mail to one recipient, waiting to be sent.</summary>
public class OutboxMail
{
    public string  EventCode  { get; set; } = "";
    public string? EntityType { get; set; }
    public long?   EntityId   { get; set; }
    public string  ToAddress  { get; set; } = "";
    public string? ToName     { get; set; }
    public string  Subject    { get; set; } = "";
    public string  BodyHtml   { get; set; } = "";
    /// <summary>Identifies this message to this recipient, so an event
    /// processed twice mails once. Globally unique.</summary>
    public string  DedupeKey  { get; set; } = "";
}

public class OutboxRow
{
    public long      OutboxId      { get; set; }
    public string    EventCode     { get; set; } = "";
    public string    ToAddress     { get; set; } = "";
    public string?   ToName        { get; set; }
    public string    Subject       { get; set; } = "";
    public string    StatusCode    { get; set; } = "";
    public int       Attempts      { get; set; }
    public string?   LastError     { get; set; }
    public DateTime  CreatedAt     { get; set; }
    public DateTime  NextAttemptAt { get; set; }
    public DateTime? SentAt        { get; set; }
}

public static class OutboxStatus
{
    public const string Pending = "Pending";
    public const string Sent    = "Sent";
    public const string Failed  = "Failed";
}

/// <summary>
/// The mail outbox (M45): reliable e-mail for survey events, and the log of
/// what was sent.
///
/// A message is written inside the transaction of the event that causes it
/// and sent afterwards by <see cref="MailOutboxWorker"/>. So the business
/// change never waits on the mail server and is never undone by it; a message
/// cannot be lost because the server was down; and a failure is recorded and
/// retried -- after 1, 5, 15 and 60 minutes -- before the row is marked Failed
/// for a person to resend.
/// </summary>
public interface IMailOutbox
{
    Task EnqueueAsync(SqlConnection c, SqlTransaction tx, OutboxMail mail);
    /// <summary>Sends the messages that are due. Returns how many were attempted.</summary>
    Task<int> DispatchDueAsync(int max, CancellationToken ct = default);
    /// <summary>Sends one message now, whatever its schedule -- the Resend
    /// button, including for a Failed row. Returns whether it was sent.</summary>
    Task<bool> DispatchOneAsync(long outboxId, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxRow>> ListForEntityAsync(string entityType, long entityId);
    Task<OutboxRow?> GetAsync(long outboxId);
}

public class MailOutbox : IMailOutbox
{
    /// <summary>Attempts before a message is marked Failed.</summary>
    public const int MaxAttempts = 5;

    private readonly string _cs;
    private readonly IEmailService _email;
    private readonly ILogger<MailOutbox> _log;

    public MailOutbox(IConfiguration config, IEmailService email, ILogger<MailOutbox> log)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _email = email;
        _log = log;
    }

    /// <summary>
    /// When a new message first becomes due; null means at once. The test host
    /// overrides this to park its messages far in the future: the tests share
    /// the live table with the deployed worker, which must never pick one up.
    /// </summary>
    protected virtual DateTime? FirstAttemptUtc => null;

    public async Task EnqueueAsync(SqlConnection c, SqlTransaction tx, OutboxMail m)
    {
        if (string.IsNullOrWhiteSpace(m.ToAddress)) return;
        var subject = m.Subject.Length > 300 ? m.Subject[..300] : m.Subject;
        // NOT EXISTS under an update lock, not the unique key alone: a
        // constraint violation would be raised inside the caller's transaction.
        await c.ExecuteAsync(@"
            INSERT INTO qms_mail_outbox
                (event_code, entity_type, entity_id, to_address, to_name, subject, body_html, dedupe_key, next_attempt_at)
            SELECT @EventCode, @EntityType, @EntityId, @to, @ToName, @subject, @BodyHtml, @DedupeKey,
                   COALESCE(@first, SYSUTCDATETIME())
            WHERE  NOT EXISTS (SELECT 1 FROM qms_mail_outbox WITH (UPDLOCK, HOLDLOCK)
                               WHERE dedupe_key = @DedupeKey)",
            new
            {
                m.EventCode, m.EntityType, m.EntityId, to = m.ToAddress.Trim(), m.ToName,
                subject, m.BodyHtml, m.DedupeKey, first = FirstAttemptUtc
            }, tx);
    }

    private sealed class Claimed
    {
        public long   OutboxId  { get; set; }
        public string ToAddress { get; set; } = "";
        public string Subject   { get; set; } = "";
        public string BodyHtml  { get; set; } = "";
        public int    Attempts  { get; set; }
    }

    // The attempt is counted, and the next one scheduled, BEFORE the send --
    // in the statement that claims the row. A crash in mid-send therefore
    // costs one attempt instead of sending the same message in a loop, and
    // READPAST keeps two workers off the same row.
    private const string Backoff = @"
        DATEADD(MINUTE, CASE attempts WHEN 0 THEN 1 WHEN 1 THEN 5 WHEN 2 THEN 15 ELSE 60 END, SYSUTCDATETIME())";

    private const string ClaimedColumns = @"
        inserted.outbox_id AS OutboxId, inserted.to_address AS ToAddress, inserted.subject AS Subject,
        inserted.body_html AS BodyHtml, inserted.attempts AS Attempts";

    public async Task<int> DispatchDueAsync(int max, CancellationToken ct = default)
    {
        var attempted = 0;
        while (attempted < max && !ct.IsCancellationRequested)
        {
            Claimed? row;
            using (var c = new SqlConnection(_cs))
            {
                row = await c.QuerySingleOrDefaultAsync<Claimed>(@"
                    ;WITH due AS (
                        SELECT TOP (1) *
                        FROM   qms_mail_outbox WITH (UPDLOCK, READPAST, ROWLOCK)
                        WHERE  status_code = 'Pending' AND next_attempt_at <= SYSUTCDATETIME()
                        ORDER  BY next_attempt_at, outbox_id)
                    UPDATE due
                    SET    next_attempt_at = " + Backoff + @", attempts = attempts + 1
                    OUTPUT " + ClaimedColumns + ";");
            }
            if (row == null) break;
            await SendAsync(row, ct);
            attempted++;
        }
        return attempted;
    }

    public async Task<bool> DispatchOneAsync(long outboxId, CancellationToken ct = default)
    {
        Claimed? row;
        using (var c = new SqlConnection(_cs))
        {
            // A row scheduled further out than the backoff keeps its schedule
            // (that is what keeps a parked test message parked if its send fails).
            row = await c.QuerySingleOrDefaultAsync<Claimed>(@"
                UPDATE qms_mail_outbox
                SET    status_code = 'Pending',
                       next_attempt_at = CASE WHEN next_attempt_at > " + Backoff + @"
                                              THEN next_attempt_at ELSE " + Backoff + @" END,
                       attempts = attempts + 1
                OUTPUT " + ClaimedColumns + @"
                WHERE  outbox_id = @outboxId AND status_code IN ('Pending', 'Failed');", new { outboxId });
        }
        return row != null && await SendAsync(row, ct);
    }

    private async Task<bool> SendAsync(Claimed row, CancellationToken ct)
    {
        bool ok;
        string? error = null;
        try
        {
            ok = await _email.SendAsync(row.ToAddress, row.Subject, row.BodyHtml, ct);
            if (!ok) error = "The mail server did not accept the message. The application log has the reason.";
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }

        try
        {
            using var c = new SqlConnection(_cs);
            if (ok)
            {
                await c.ExecuteAsync(@"
                    UPDATE qms_mail_outbox
                    SET    status_code = 'Sent', sent_at = SYSUTCDATETIME(), last_error = NULL
                    WHERE  outbox_id = @id", new { id = row.OutboxId });
            }
            else
            {
                if (error is { Length: > 1000 }) error = error[..1000];
                await c.ExecuteAsync(@"
                    UPDATE qms_mail_outbox
                    SET    last_error  = @error,
                           status_code = CASE WHEN attempts >= @max THEN 'Failed' ELSE 'Pending' END
                    WHERE  outbox_id = @id", new { id = row.OutboxId, error, max = MaxAttempts });
                _log.LogWarning("Outbox mail {OutboxId} to {To} failed (attempt {Attempt}): {Error}",
                    row.OutboxId, row.ToAddress, row.Attempts, error);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not record the result of outbox mail {OutboxId}", row.OutboxId);
        }
        return ok;
    }

    private const string RowColumns = @"
        outbox_id AS OutboxId, event_code AS EventCode, to_address AS ToAddress, to_name AS ToName,
        subject AS Subject, status_code AS StatusCode, attempts AS Attempts, last_error AS LastError,
        created_at AS CreatedAt, next_attempt_at AS NextAttemptAt, sent_at AS SentAt";

    public async Task<IReadOnlyList<OutboxRow>> ListForEntityAsync(string entityType, long entityId)
    {
        using var c = new SqlConnection(_cs);
        return (await c.QueryAsync<OutboxRow>(
            "SELECT " + RowColumns + @"
             FROM   qms_mail_outbox
             WHERE  entity_type = @entityType AND entity_id = @entityId
             ORDER  BY outbox_id DESC", new { entityType, entityId })).ToList();
    }

    public async Task<OutboxRow?> GetAsync(long outboxId)
    {
        using var c = new SqlConnection(_cs);
        return await c.QuerySingleOrDefaultAsync<OutboxRow>(
            "SELECT " + RowColumns + " FROM qms_mail_outbox WHERE outbox_id = @outboxId", new { outboxId });
    }
}

/// <summary>Sends what is due in the outbox, every half minute.</summary>
public class MailOutboxWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MailOutboxWorker> _log;

    public MailOutboxWorker(IServiceScopeFactory scopes, ILogger<MailOutboxWorker> log)
    {
        _scopes = scopes;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the application finish starting before the first pass.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMailOutbox>().DispatchDueAsync(25, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Mail outbox pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
