namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Pre-warms the AD user-list cache so admins never wait for the cold
/// LDAP fetch when they open the "Add user from AD" picker. Runs once on
/// app startup and then on a short timer so the cache is always refreshed
/// before its 30-minute TTL expires.
///
/// No-op when AD is not configured -- the picker shows a "configure AD
/// first" hint in that case, and we don't want this service to keep
/// retrying an unconfigured directory.
/// </summary>
public class AdCachePrimingService : BackgroundService
{
    // 25 min keeps the cache continuously warm (the cache TTL inside
    // AdService is 30 minutes).
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(25);

    private readonly IServiceProvider _services;
    private readonly ILogger<AdCachePrimingService> _log;

    public AdCachePrimingService(IServiceProvider services, ILogger<AdCachePrimingService> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish its own startup work before hitting AD.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PrimeOnceAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "AD cache prime tick failed"); }
            try { await Task.Delay(RefreshInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PrimeOnceAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var ad       = scope.ServiceProvider.GetRequiredService<IAdService>();

        var cfg = await settings.GetAdConfigAsync();
        if (!cfg.IsConfigured)
        {
            _log.LogDebug("AD cache prime: AD not configured, skipping");
            return;
        }

        var started = DateTime.UtcNow;
        var users = await ad.ListUsersAsync(cfg, filter: null, max: int.MaxValue);
        _log.LogInformation("AD cache primed: {Count} users in {Ms} ms",
            users.Count, (int)(DateTime.UtcNow - started).TotalMilliseconds);
    }
}
