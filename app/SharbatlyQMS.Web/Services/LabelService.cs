using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services;

/// <summary>One editable label as the Admin → Labels page shows it.</summary>
public sealed record UiLabel(
    string Key, string? ScreenKey, string? Kind, string? CustomText, DateTime LastSeenAt,
    DateTime? UpdatedAt, string? UpdatedBy)
{
    /// <summary>What sort of thing this label is -- Field label, Column header,
    /// Button and so on. Derived from the element it sits in by the wrapper and
    /// seeded; a label DISCOVERED at runtime has none, because the renderer sees
    /// the text and not the markup around it. Shown as "Other".</summary>
    public string KindLabel => string.IsNullOrWhiteSpace(Kind) ? "Other" : Kind!;

    /// <summary>What the screen actually prints today.</summary>
    public string Effective => string.IsNullOrWhiteSpace(CustomText) ? Key : CustomText!;
    public bool   IsOverridden => !string.IsNullOrWhiteSpace(CustomText);
}

public interface ILabelService
{
    /// <summary>
    /// The text to print for a shipped English label. Returns the
    /// administrator's replacement when one exists, otherwise the original —
    /// so a view reads correctly whether or not anything has been customised,
    /// and an empty label table changes nothing.
    ///
    /// Also records the label as seen, which is what populates the admin page
    /// with the labels a screen really uses rather than a hand-kept list.
    /// </summary>
    string this[string defaultText] { get; }

    /// <summary>Same as the indexer, with an explicit screen for grouping when
    /// the caller knows better than the current route (partials, layout).</summary>
    string Text(string defaultText, string? screenKey);

    /// <summary>Every known label, newest overrides first. Feeds the admin page.</summary>
    Task<IReadOnlyList<UiLabel>> ListAsync(CancellationToken ct = default);

    /// <summary>Sets or clears one override. A null/blank value resets the
    /// label to the English the code ships.</summary>
    Task SaveAsync(string key, string? customText, string user, CancellationToken ct = default);

    /// <summary>Clears every override on a screen (or all of them when
    /// <paramref name="screenKey"/> is null). Returns rows reset.</summary>
    Task<int> ResetAsync(string? screenKey, string user, CancellationToken ct = default);

    /// <summary>Writes newly-seen labels to the database and reloads the
    /// snapshot. Called by the background flusher and before the admin page
    /// renders, so opening it always shows everything seen so far.</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Reload the override snapshot. Called after every save.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// Singleton holding the label overrides as one immutable dictionary behind a
/// <c>volatile</c> reference, swapped in a single assignment — the same shape
/// as <c>PermissionResolver</c>, and for the same reason: this is read several
/// dozen times per rendered page, so readers must never take a lock and must
/// never touch the database.
///
/// Labels are discovered, not registered. Rendering one records it in an
/// in-memory buffer; a background flush writes the new keys. That means the
/// admin page lists exactly what the application actually printed, and adding a
/// label to a view needs no second step that somebody will forget.
/// </summary>
public sealed class LabelService : ILabelService
{
    /// <summary>Longer than this is a sentence, not a label — passed straight
    /// through, and never stored (the key column is 200).</summary>
    private const int MaxKeyLength = 200;

    private readonly string _cs;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<LabelService> _log;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // key -> custom text. Only OVERRIDDEN labels live here: the common case is
    // a lookup miss returning the default, so the dictionary stays small
    // however many labels the application has.
    private volatile IReadOnlyDictionary<string, string> _overrides =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Labels seen since the last flush: key -> screen it was seen on.
    private readonly ConcurrentDictionary<string, string?> _seen = new(StringComparer.Ordinal);

    public LabelService(IConfiguration cfg, IHttpContextAccessor http, ILogger<LabelService> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _http = http;
        _log  = log;
    }

    public string this[string defaultText] => Text(defaultText, null);

    public string Text(string defaultText, string? screenKey)
    {
        if (string.IsNullOrEmpty(defaultText)) return defaultText;
        if (defaultText.Length > MaxKeyLength) return defaultText;

        // Record before returning, so a label is discoverable from its very
        // first render even if nobody has customised anything yet.
        _seen.TryAdd(defaultText, screenKey ?? CurrentScreen());

        return _overrides.TryGetValue(defaultText, out var custom) && custom.Length > 0
            ? custom
            : defaultText;
    }

    /// <summary>The controller currently rendering, used to group the admin
    /// page. Best-effort: a label rendered outside a request (or from the
    /// layout before routing resolves) is simply left ungrouped.</summary>
    private string? CurrentScreen()
    {
        var rd = _http.HttpContext?.GetRouteData();
        var controller = rd?.Values.TryGetValue("controller", out var c) == true ? c?.ToString() : null;
        return string.IsNullOrWhiteSpace(controller) ? null : controller;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_seen.IsEmpty) { await RefreshAsync(ct); return; }

        // Snapshot and clear first: a label seen during the flush belongs to the
        // next one, and dropping it would only mean it is rediscovered on the
        // next render anyway.
        var batch = _seen.ToArray();
        _seen.Clear();

        try
        {
            using var c = new SqlConnection(_cs);
            await c.OpenAsync(ct);
            // MERGE rather than INSERT-if-missing: an existing label only needs
            // last_seen_at refreshed, and its custom_text must survive untouched.
            const string sql = @"
                MERGE qms_ui_label AS T
                USING (SELECT @Key AS label_key) AS S ON T.label_key = S.label_key
                WHEN MATCHED THEN UPDATE SET
                    last_seen_at = SYSUTCDATETIME(),
                    screen_key   = COALESCE(T.screen_key, @Screen)
                WHEN NOT MATCHED THEN
                    INSERT (label_key, screen_key) VALUES (@Key, @Screen);";
            foreach (var (key, screen) in batch)
                await c.ExecuteAsync(sql, new { Key = key, Screen = screen });
        }
        catch (Exception ex)
        {
            // Discovery is a convenience, never a reason to fail a page or a
            // background tick. The labels are still rendered from their
            // defaults, and the next flush picks them up again.
            _log.LogWarning(ex, "UI label flush failed ({Count} labels); they will be rediscovered.", batch.Length);
            foreach (var (key, screen) in batch) _seen.TryAdd(key, screen);
        }

        await RefreshAsync(ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            using var c = new SqlConnection(_cs);
            var rows = await c.QueryAsync<(string label_key, string custom_text)>(
                "SELECT label_key, custom_text FROM qms_ui_label WHERE custom_text IS NOT NULL AND custom_text <> ''");
            _overrides = rows.ToDictionary(r => r.label_key, r => r.custom_text, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Keep the previous snapshot. Reverting to defaults on a transient
            // database blip would rename every screen back under the user.
            _log.LogError(ex, "UI label refresh failed; keeping the previous overrides.");
        }
        finally { _loadGate.Release(); }
    }

    public async Task<IReadOnlyList<UiLabel>> ListAsync(CancellationToken ct = default)
    {
        using var c = new SqlConnection(_cs);
        var rows = await c.QueryAsync<UiLabel>(@"
            SELECT label_key    AS [Key],
                   screen_key   AS ScreenKey,
                   kind         AS Kind,
                   custom_text  AS CustomText,
                   last_seen_at AS LastSeenAt,
                   updated_at   AS UpdatedAt,
                   updated_by   AS UpdatedBy
            FROM   qms_ui_label
            ORDER  BY screen_key, kind, label_key");
        return rows.ToList();
    }

    public async Task SaveAsync(string key, string? customText, string user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        // Blank means "reset": storing an empty override would print an empty
        // button, which reads as a broken page rather than as a cleared setting.
        var value = string.IsNullOrWhiteSpace(customText) ? null : customText.Trim();
        if (value != null && value.Length > 400) value = value[..400];
        // A replacement identical to the default is not an override; storing it
        // would leave the admin page showing an edit that changes nothing.
        if (string.Equals(value, key, StringComparison.Ordinal)) value = null;

        using var c = new SqlConnection(_cs);
        await c.ExecuteAsync(@"
            MERGE qms_ui_label AS T
            USING (SELECT @Key AS label_key) AS S ON T.label_key = S.label_key
            WHEN MATCHED THEN UPDATE SET
                custom_text = @Value, updated_at = SYSUTCDATETIME(), updated_by = @User
            WHEN NOT MATCHED THEN
                INSERT (label_key, custom_text, updated_at, updated_by)
                VALUES (@Key, @Value, SYSUTCDATETIME(), @User);",
            new { Key = key, Value = value, User = user });

        await RefreshAsync(ct);
    }

    public async Task<int> ResetAsync(string? screenKey, string user, CancellationToken ct = default)
    {
        using var c = new SqlConnection(_cs);
        var n = await c.ExecuteAsync(@"
            UPDATE qms_ui_label
            SET    custom_text = NULL, updated_at = SYSUTCDATETIME(), updated_by = @User
            WHERE  custom_text IS NOT NULL
              AND (@Screen IS NULL OR screen_key = @Screen)",
            new { Screen = string.IsNullOrWhiteSpace(screenKey) ? null : screenKey, User = user });
        await RefreshAsync(ct);
        return n;
    }
}

/// <summary>
/// Writes newly-discovered labels to the database off the request path. A
/// render only touches an in-memory dictionary; this turns that into rows.
///
/// Deliberately slow (every 60s) and best-effort: discovery lagging a minute
/// behind costs nothing, and the admin page flushes explicitly before it
/// renders so it never shows a stale list.
/// </summary>
public sealed class LabelDiscoveryService : BackgroundService
{
    private readonly ILabelService _labels;
    private readonly ILogger<LabelDiscoveryService> _log;

    public LabelDiscoveryService(ILabelService labels, ILogger<LabelDiscoveryService> log)
    {
        _labels = labels;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One load at startup so the very first rendered page already honours
        // the overrides -- otherwise every screen would flash its English name
        // once after each restart.
        await _labels.RefreshAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await _labels.FlushAsync(stoppingToken); }
            catch (Exception ex) { _log.LogWarning(ex, "UI label discovery tick failed."); }
        }
    }
}
