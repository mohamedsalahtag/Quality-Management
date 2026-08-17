using System.Globalization;
using System.Net;
using SharbatlyQMS.Web.Models.Security;   // RoleCodes

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Periodically checks the disk partition that holds the uploaded-photos folder
/// and, when it reaches the admin-configured threshold (Alert thresholds →
/// "Uploads disk full %"), emails every active admin so they can free space or
/// move the uploads folder to a bigger disk (Site Configuration → Storage).
///
/// Throttled to at most one alert per 24h while over threshold; the throttle
/// marker (a settings row) is cleared once usage drops back under, so a later
/// breach re-alerts. Follows the house background-service pattern
/// (<see cref="AdCachePrimingService"/>): a while-loop with a per-tick DI scope.
/// </summary>
public class DiskSpaceMonitorService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<DiskSpaceMonitorService> _log;

    public DiskSpaceMonitorService(IServiceProvider services, IWebHostEnvironment env,
        IConfiguration config, ILogger<DiskSpaceMonitorService> log)
    {
        _services = services; _env = env; _config = config; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish its own startup before touching the disk / DB.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Disk-space check tick failed"); }
            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var db       = scope.ServiceProvider.GetRequiredService<IDbService>();
        var email    = scope.ServiceProvider.GetRequiredService<IEmailService>();

        var alerts    = await settings.GetAlertConfigAsync();
        var threshold = Math.Clamp(alerts.DiskFullPercent <= 0 ? 90 : alerts.DiskFullPercent, 50, 99);

        // The partition that actually holds the (possibly relocated) uploads folder.
        var root      = UploadStorage.Root(_env, _config);
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(driveRoot)) return;

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(driveRoot);
            if (!drive.IsReady) return;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Disk-space check: cannot read drive for {Root}", root); return; }

        long total = drive.TotalSize;
        long free  = drive.AvailableFreeSpace;
        if (total <= 0) return;
        int usedPct = (int)Math.Round((double)(total - free) / total * 100);

        var lastRaw = await db.GetConfigAsync(SettingKeys.StorageDiskAlertLastSent);

        if (usedPct < threshold)
        {
            // Under threshold — reset the marker so the next breach re-alerts.
            if (!string.IsNullOrEmpty(lastRaw))
                await db.SetConfigAsync(SettingKeys.StorageDiskAlertLastSent, "", null);
            return;
        }

        // Over threshold — throttle to at most once per 24h.
        if (DateTime.TryParse(lastRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var lastSent)
            && (DateTime.UtcNow - lastSent) < TimeSpan.FromHours(24))
            return;

        var recipients = (await db.ListUsersAsync(null, RoleCodes.Admin, true))
            .Select(u => u.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            _log.LogWarning("Uploads disk {Pct}% full (threshold {Threshold}%) but no active admin email addresses to notify.", usedPct, threshold);
            return;
        }

        static string Gb(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);
        var subject = $"SharbatlyQMS: uploads disk almost full ({usedPct}%)";
        var body =
            $"<p>The disk holding the SharbatlyQMS uploaded inspection photos is <strong>{usedPct}% full</strong> " +
            $"(alert threshold {threshold}%).</p>" +
            "<ul>" +
            $"<li>Uploads folder: {WebUtility.HtmlEncode(root)}</li>" +
            $"<li>Drive: {WebUtility.HtmlEncode(drive.Name)}</li>" +
            $"<li>Used {Gb(total - free)} GB of {Gb(total)} GB &mdash; {Gb(free)} GB free</li>" +
            "</ul>" +
            "<p>Please free up space, or move the uploads folder to a larger disk from " +
            "<strong>Site Configuration &rarr; Storage</strong>.</p>";

        int sent = 0;
        foreach (var to in recipients)
        {
            try { if (await email.SendAsync(to, subject, body, ct)) sent++; }
            catch (Exception ex) { _log.LogWarning(ex, "Disk-full alert email to {To} failed", to); }
        }
        _log.LogWarning("Uploads disk {Pct}% full. Alert emailed to {Sent}/{Total} admins.", usedPct, sent, recipients.Count);

        // Record the send time so we don't re-alert for another 24h.
        await db.SetConfigAsync(SettingKeys.StorageDiskAlertLastSent,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), null);
    }
}
