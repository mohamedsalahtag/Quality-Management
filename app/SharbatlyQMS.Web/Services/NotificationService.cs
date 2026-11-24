using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services;

/// <summary>One event, as the bell shows it.</summary>
public class AppNotification
{
    public string  EventCode  { get; set; } = "";
    public string  Title      { get; set; } = "";
    public string? Body       { get; set; }
    public string? EntityType { get; set; }
    public long?   EntityId   { get; set; }
    /// <summary>Identifies the event, so processing it twice notifies once.
    /// Unique per recipient.</summary>
    public string  DedupeKey  { get; set; } = "";
}

/// <summary>Who gets it, and where it takes them. The link is per recipient
/// because two people told about the same event may not be able to open the
/// same page (an inspector goes to the arrival, a decider to the survey).</summary>
public record NotificationTarget(int UserId, string? LinkUrl);

public class NotificationRow
{
    public long      NotificationId { get; set; }
    public string    EventCode      { get; set; } = "";
    public string    Title          { get; set; } = "";
    public string?   Body           { get; set; }
    public string?   LinkUrl        { get; set; }
    public DateTime  CreatedAt      { get; set; }
    public DateTime? ReadAt         { get; set; }
    public bool      IsUnread => !ReadAt.HasValue;
}

/// <summary>
/// The bell in the top bar (M45): per-user, in-app notifications. A row is
/// written inside the transaction of the event that causes it, so a rolled
/// back change notifies nobody and a committed one always does.
/// </summary>
public interface INotificationService
{
    Task AddAsync(SqlConnection c, SqlTransaction tx, AppNotification n, IEnumerable<NotificationTarget> targets);
    Task<int> UnreadCountAsync(int userId);
    Task<IReadOnlyList<NotificationRow>> ListAsync(int userId, int take);
    /// <summary>Marks one of the user's own notifications read and returns it
    /// (null when it is not theirs).</summary>
    Task<NotificationRow?> MarkReadAsync(int userId, long notificationId);
    Task<int> MarkAllReadAsync(int userId);
}

public class NotificationService : INotificationService
{
    private readonly string _cs;

    public NotificationService(IConfiguration config)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
    }

    public async Task AddAsync(SqlConnection c, SqlTransaction tx, AppNotification n,
        IEnumerable<NotificationTarget> targets)
    {
        var title = n.Title.Length > 200 ? n.Title[..200] : n.Title;
        var body  = n.Body is { Length: > 500 } ? n.Body[..500] : n.Body;
        foreach (var t in targets.GroupBy(x => x.UserId).Select(g => g.First()))
        {
            // NOT EXISTS under an update lock rather than relying on the unique
            // key: a constraint violation here would be raised inside the
            // caller's business transaction.
            await c.ExecuteAsync(@"
                INSERT INTO qms_notification
                    (user_id, event_code, title, body, link_url, entity_type, entity_id, dedupe_key)
                SELECT @UserId, @EventCode, @title, @body, @LinkUrl, @EntityType, @EntityId, @DedupeKey
                WHERE  NOT EXISTS (SELECT 1 FROM qms_notification WITH (UPDLOCK, HOLDLOCK)
                                   WHERE user_id = @UserId AND dedupe_key = @DedupeKey)",
                new
                {
                    t.UserId, n.EventCode, title, body, t.LinkUrl,
                    n.EntityType, n.EntityId, n.DedupeKey
                }, tx);
        }
    }

    public async Task<int> UnreadCountAsync(int userId)
    {
        using var c = new SqlConnection(_cs);
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qms_notification WHERE user_id = @userId AND read_at IS NULL", new { userId });
    }

    public async Task<IReadOnlyList<NotificationRow>> ListAsync(int userId, int take)
    {
        using var c = new SqlConnection(_cs);
        return (await c.QueryAsync<NotificationRow>(@"
            SELECT TOP (@take)
                   notification_id AS NotificationId, event_code AS EventCode, title AS Title, body AS Body,
                   link_url AS LinkUrl, created_at AS CreatedAt, read_at AS ReadAt
            FROM   qms_notification
            WHERE  user_id = @userId
            ORDER  BY notification_id DESC", new { userId, take = Math.Clamp(take, 1, 500) })).ToList();
    }

    public async Task<NotificationRow?> MarkReadAsync(int userId, long notificationId)
    {
        using var c = new SqlConnection(_cs);
        // user_id is part of the WHERE: nobody can read or mark another
        // person's notification by guessing its number.
        return await c.QuerySingleOrDefaultAsync<NotificationRow>(@"
            UPDATE qms_notification
            SET    read_at = COALESCE(read_at, SYSUTCDATETIME())
            OUTPUT inserted.notification_id AS NotificationId, inserted.event_code AS EventCode,
                   inserted.title AS Title, inserted.body AS Body, inserted.link_url AS LinkUrl,
                   inserted.created_at AS CreatedAt, inserted.read_at AS ReadAt
            WHERE  notification_id = @notificationId AND user_id = @userId",
            new { notificationId, userId });
    }

    public async Task<int> MarkAllReadAsync(int userId)
    {
        using var c = new SqlConnection(_cs);
        return await c.ExecuteAsync(@"
            UPDATE qms_notification SET read_at = SYSUTCDATETIME()
            WHERE  user_id = @userId AND read_at IS NULL", new { userId });
    }
}
