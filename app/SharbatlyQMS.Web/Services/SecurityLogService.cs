using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The Users &amp; security feed: every change to who exists and what they can
/// reach, from both places the system records such things.
///
/// Why a merge rather than one table. <c>qms_permission_log</c> is the readable
/// record — "QcOperator, Arrivals.Details, Read → Edit" — but it only starts on
/// 20 Aug 2026 and only covers grants, role assignment, plant access and account
/// state. Account creation, deletion and password resets were never written to
/// it, and neither was anything at all before that date; those live only in
/// <c>qms_audit_log</c> as JSON. Reading one table answers half the question.
///
/// The two overlap for the events both record (a role change writes to both), so
/// the audit row is dropped when a permission-log row already describes the same
/// actor acting on the same subject at the same moment — see
/// <see cref="CorrelationWindow"/>. The readable row wins; nothing is lost.
/// </summary>
public interface ISecurityLogService
{
    Task<IReadOnlyList<SecurityEvent>> ListAsync(SecurityLogFilter filter);
    Task<SecurityLogOptions> GetOptionsAsync();
}

public class SecurityLogService : ISecurityLogService
{
    private readonly string _cs;
    private readonly IPermissionLogService _permLog;

    public SecurityLogService(IConfiguration cfg, IPermissionLogService permLog)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _permLog = permLog;
    }

    /// <summary>
    /// How close in time two rows must be to count as the same event recorded
    /// twice. The two writes happen inside one request — usually inside one
    /// transaction — so anything beyond a few seconds is a different event by a
    /// person who happens to be working quickly.
    /// </summary>
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<SecurityEvent>> ListAsync(SecurityLogFilter f)
    {
        var take = Math.Clamp(f.PageSize <= 0 ? 200 : f.PageSize, 1, 1000);

        var wantsPermissionLog = string.IsNullOrWhiteSpace(f.Category)
                                 || SecurityCategories.FromPermissionLog.Contains(f.Category);
        var wantsAuditLog      = string.IsNullOrWhiteSpace(f.Category)
                                 || SecurityCategories.IsAuditOnly(f.Category);

        var events = new List<SecurityEvent>();

        if (wantsPermissionLog)
            events.AddRange(FromPermissionLog(await _permLog.ListAsync(new PermissionLogFilter
            {
                Actor       = f.Actor,
                SubjectKey  = f.SubjectKey,
                SubjectType = f.SubjectType,
                ChangeType  = f.Category,
                Search      = f.Search,
                From        = f.From,
                To          = f.To,
                // Over-fetch: the merge drops rows, and a short page here would
                // silently truncate the combined result.
                PageSize    = Math.Min(take * 2, 1000)
            })));

        if (wantsAuditLog)
            events.AddRange(await FromAuditLogAsync(f, take));

        // Drop the raw audit row when the readable log already tells this story.
        var readable = events.Where(e => !e.FromAuditLog).ToList();
        var merged = events
            .Where(e => !e.FromAuditLog || !readable.Any(r =>
                r.CorrelationKey == e.CorrelationKey &&
                (r.ChangedAt - e.ChangedAt).Duration() <= CorrelationWindow))
            .ToList();

        // Filters that can only be applied once the audit side has been resolved
        // into subjects and sentences.
        if (!string.IsNullOrWhiteSpace(f.SubjectKey))
            merged = merged.Where(e => string.Equals(e.SubjectKey, f.SubjectKey.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(f.SubjectType))
            merged = merged.Where(e => string.Equals(e.SubjectType, f.SubjectType.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(f.Category))
            merged = merged.Where(e => string.Equals(e.Category, f.Category.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var q = f.Search.Trim();
            merged = merged.Where(e =>
                Has(e.Headline, q) || Has(e.SubjectKey, q) || Has(e.SubjectName, q) ||
                Has(e.ChangedBy, q) || Has(e.ItemCode, q) ||
                e.Details.Any(d => Has(d.Field, q) || Has(d.Old, q) || Has(d.New, q))).ToList();
        }

        return merged
            .OrderByDescending(e => e.ChangedAt)
            .ThenByDescending(e => e.AuditId ?? 0)
            .Take(take)
            .ToList();

        static bool Has(string? haystack, string needle) =>
            haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    // ---- The readable table ------------------------------------------------

    private static IEnumerable<SecurityEvent> FromPermissionLog(IReadOnlyList<PermissionLogEntry> rows) =>
        rows.Select(e =>
        {
            var subject = string.IsNullOrWhiteSpace(e.SubjectName) ? e.SubjectKey : e.SubjectName;
            var item    = string.IsNullOrWhiteSpace(e.ItemName) ? e.ItemCode : e.ItemName;

            var headline = e.ChangeType switch
            {
                PermissionChangeTypes.Permission =>
                    $"{e.ChangedBy} changed “{item}” for the {subject} role.",
                PermissionChangeTypes.Plant =>
                    e.NewValue == "Access granted"
                        ? $"{e.ChangedBy} gave {subject} access to {item}."
                        : $"{e.ChangedBy} removed {subject}’s access to {item}.",
                PermissionChangeTypes.RoleAssignment =>
                    $"{e.ChangedBy} moved {subject} from the {e.OldValue} role to {e.NewValue}.",
                PermissionChangeTypes.RoleCreated =>
                    $"{e.ChangedBy} created the {subject} role. {e.NewValue}".Trim(),
                PermissionChangeTypes.RoleDeleted =>
                    string.IsNullOrWhiteSpace(e.ItemCode)
                        ? $"{e.ChangedBy} deleted the {subject} role, which held {e.OldValue}."
                        : $"{e.ChangedBy} deleted the {subject} role — “{item}” was revoked with it.",
                PermissionChangeTypes.RoleSetting =>
                    $"{e.ChangedBy} changed {item} on the {subject} role.",
                PermissionChangeTypes.Account =>
                    e.NewValue == "Account deleted"
                        ? $"{e.ChangedBy} deleted the account {subject}."
                        : $"{e.ChangedBy} set {subject}’s account to {e.NewValue}.",
                _ => $"{e.ChangedBy} changed {item} on {subject}."
            };

            // A permission-log row is already one field moving, so it is its own
            // single before/after pair — no JSON to open up.
            var details = new List<AuditNarrator.FriendlyDiff>();
            if (!string.IsNullOrWhiteSpace(e.OldValue) || !string.IsNullOrWhiteSpace(e.NewValue))
            {
                var isGrant = e.ChangeType == PermissionChangeTypes.Permission;
                var oldV = isGrant ? AuditNarrator.AccessLevelLabel(e.OldValue) : (e.OldValue ?? "(empty)");
                var newV = isGrant ? AuditNarrator.AccessLevelLabel(e.NewValue) : (e.NewValue ?? "(empty)");
                details.Add(new AuditNarrator.FriendlyDiff(
                    string.IsNullOrWhiteSpace(item) ? PermissionChangeTypes.Label(e.ChangeType) : item!,
                    oldV, newV, KindOf(e.OldValue, e.NewValue)));
            }

            return new SecurityEvent
            {
                ChangedAt    = e.ChangedAt,
                ChangedBy    = e.ChangedBy,
                Category     = e.ChangeType,
                SubjectType  = e.SubjectType,
                SubjectKey   = e.SubjectKey,
                SubjectName  = e.SubjectName,
                Headline     = headline,
                Details      = details,
                ItemCode     = e.ItemCode,
                SourceIp     = e.SourceIp,
                FromAuditLog = false
            };
        });

    private static AuditNarrator.DiffKind KindOf(string? o, string? n)
    {
        var hadOld = !string.IsNullOrWhiteSpace(o);
        var hasNew = !string.IsNullOrWhiteSpace(n);
        if (!hadOld && hasNew) return AuditNarrator.DiffKind.Added;
        if (hadOld && !hasNew) return AuditNarrator.DiffKind.Removed;
        return AuditNarrator.DiffKind.Changed;
    }

    // ---- The raw table -----------------------------------------------------

    /// <summary>
    /// User and Role rows out of the audit log, resolved into subjects and
    /// sentences. This is where account creation, deletion and password resets
    /// come from — and where the whole history before 20 Aug 2026 comes from.
    /// </summary>
    private async Task<List<SecurityEvent>> FromAuditLogAsync(SecurityLogFilter f, int take)
    {
        DateTime? fromUtc = f.From.HasValue
            ? DateTime.SpecifyKind(f.From.Value.Date, DateTimeKind.Local).ToUniversalTime()
            : null;
        DateTime? toUtc = f.To.HasValue
            ? DateTime.SpecifyKind(f.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            : null;

        using var c = new SqlConnection(_cs);
        var rows = (await c.QueryAsync<AuditEntryListRow>(@"
            SELECT TOP (@take)
                   audit_id           AS AuditId,
                   entity_type        AS EntityType,
                   entity_id          AS EntityId,
                   action_code        AS ActionCode,
                   old_values_json    AS OldValuesJson,
                   new_values_json    AS NewValuesJson,
                   changed_at         AS ChangedAt,
                   changed_by         AS ChangedBy,
                   source_ip          AS SourceIp,
                   source_user_agent  AS SourceUserAgent,
                   source_device_name AS SourceDeviceName
            FROM   qms_audit_log
            WHERE  entity_type IN @types
              AND  (@actor   IS NULL OR changed_by = @actor)
              AND  (@fromUtc IS NULL OR changed_at >= @fromUtc)
              AND  (@toUtc   IS NULL OR changed_at <  @toUtc)
            ORDER  BY changed_at DESC, audit_id DESC",
            new
            {
                // Over-fetch for the same reason the permission side does.
                take  = Math.Min(take * 3, 3000),
                types = AuditNarrator.SecurityEntityTypes,
                actor = string.IsNullOrWhiteSpace(f.Actor) ? null : f.Actor.Trim(),
                fromUtc,
                toUtc
            })).ToList();

        if (rows.Count == 0) return new List<SecurityEvent>();

        foreach (var r in rows)
            r.DiffRows = AuditService.ParseDiffs(r.OldValuesJson, r.NewValuesJson);

        // Resolve usernames in one lookup rather than per row.
        var userIds = rows.Where(r => r.EntityType == EntityTypes.User && r.EntityId > 0)
                          .Select(r => (int)r.EntityId).Distinct().ToArray();
        var names = new Dictionary<long, string>();
        if (userIds.Length > 0)
            foreach (var u in await c.QueryAsync<UserNameRow>(
                "SELECT UserId, Username FROM portal.[User] WHERE UserId IN @ids", new { ids = userIds }))
                names[u.UserId] = u.Username;

        var events = new List<SecurityEvent>(rows.Count);
        foreach (var r in rows)
        {
            var isUser = r.EntityType == EntityTypes.User;

            var subjectKey = isUser
                ? (names.TryGetValue(r.EntityId, out var n) ? n : UsernameFromJson(r) ?? $"#{r.EntityId}")
                : AuditNarrator.RoleCodeFromJson(r.NewValuesJson ?? r.OldValuesJson) ?? "(unknown role)";

            r.DisplayLabel = AuditNarrator.RecordLabel(r.EntityType, null, null, subjectKey);

            var category = (r.EntityType, r.ActionCode) switch
            {
                (EntityTypes.User, ActionCodes.Created)       => SecurityCategories.UserCreated,
                (EntityTypes.User, ActionCodes.Deleted)       => SecurityCategories.UserDeleted,
                (EntityTypes.User, ActionCodes.PasswordReset) => SecurityCategories.PasswordReset,
                (EntityTypes.User, _)                         => SecurityCategories.UserChanged,
                (EntityTypes.Role, ActionCodes.Created)       => SecurityCategories.RoleCreated,
                (EntityTypes.Role, ActionCodes.Deleted)       => SecurityCategories.RoleDeleted,
                _                                             => SecurityCategories.Permission
            };

            events.Add(new SecurityEvent
            {
                ChangedAt     = r.ChangedAt,
                ChangedBy     = r.ChangedBy,
                Category      = category,
                SubjectType   = isUser ? PermissionSubjects.User : PermissionSubjects.Role,
                SubjectKey    = subjectKey,
                SubjectName   = null,
                Headline      = AuditNarrator.Sentence(r),
                Details       = AuditNarrator.Expand(r),
                ItemCode      = null,
                SourceIp      = r.SourceIp,
                FromAuditLog  = true,
                AuditId       = r.AuditId,
                OldValuesJson = r.OldValuesJson,
                NewValuesJson = r.NewValuesJson
            });
        }
        return events;
    }

    private sealed class UserNameRow
    {
        public long   UserId   { get; set; }
        public string Username { get; set; } = "";
    }

    /// <summary>The username as it was at the time, for an account since deleted
    /// — the portal.User row is gone, but the creation payload still names it.</summary>
    private static string? UsernameFromJson(AuditEntryListRow r)
    {
        foreach (var d in r.DiffRows)
            if (d.FieldName.Equals("Username", StringComparison.OrdinalIgnoreCase) ||
                d.FieldName.Equals("username", StringComparison.Ordinal))
                return d.NewValue ?? d.OldValue;
        return null;
    }

    // ---- Filter dropdowns --------------------------------------------------

    public async Task<SecurityLogOptions> GetOptionsAsync()
    {
        var permOpts = await _permLog.GetOptionsAsync();

        // Actors and subjects that appear only in the audit log — everyone who
        // ever created a user, and every user who was created — would be missing
        // from the dropdowns if they came from the permission log alone.
        using var c = new SqlConnection(_cs);
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT changed_by
            FROM   qms_audit_log
            WHERE  entity_type IN ('User','Role') AND changed_by IS NOT NULL
            ORDER  BY changed_by;

            SELECT DISTINCT u.Username
            FROM   qms_audit_log l
            JOIN   portal.[User] u ON u.UserId = l.entity_id
            WHERE  l.entity_type = 'User'
            ORDER  BY u.Username;");

        var auditActors = (await grid.ReadAsync<string>()).ToList();
        var auditUsers  = (await grid.ReadAsync<string>()).ToList();

        var actors = permOpts.Actors.Concat(auditActors)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subjects = permOpts.Subjects
            .Concat(auditUsers.Select(u => new PermissionLogSubject(u, PermissionSubjects.User, null)))
            .GroupBy(s => s.SubjectType + "|" + s.SubjectKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.SubjectType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SubjectKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SecurityLogOptions
        {
            Actors     = actors,
            Subjects   = subjects,
            Categories = SecurityCategories.All
        };
    }
}
