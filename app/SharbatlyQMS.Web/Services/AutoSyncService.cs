using System.Collections.Concurrent;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Background scheduler that runs each enabled SAP master-data sync at its
/// own scheduled hours. Per-endpoint configuration lives in
/// <c>Sap.Sync.{key}.{Enabled|Hours|LastRunUtc}</c>.
///
/// Adapted from ProductionControl.Services.AutoMaterialSyncService -- same
/// poll-every-minute + per-hour suppression-window pattern, generalized to
/// iterate over multiple endpoints (Material Master, Vendor Master).
/// </summary>
public class AutoSyncService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AutoSyncService> _log;

    private static readonly TimeSpan PollInterval      = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SuppressionWindow = TimeSpan.FromMinutes(50);

    // Tracks endpoints whose sync is currently in flight so a long-running
    // SyncAsync (e.g. SAP fetching tens of thousands of rows) cannot be
    // re-entered by the next tick or by an admin-triggered sync. Adding a
    // key returns false if already present; that's the overlap guard.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public AutoSyncService(IServiceProvider services, ILogger<AutoSyncService> log)
    {
        _services = services; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Auto sync tick failed"); }
            try { await Task.Delay(PollInterval, stoppingToken); } catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var sync     = scope.ServiceProvider.GetRequiredService<ISapSyncService>();
        var nowLocal = DateTime.Now;

        foreach (var key in SyncableEndpoints.All)
        {
            try
            {
                var cfg = await settings.GetEndpointSyncAsync(key);
                if (!cfg.Enabled || cfg.Hours.Count == 0) continue;
                if (!cfg.Hours.Contains(nowLocal.Hour)) continue;
                if (cfg.LastRunUtc.HasValue &&
                    DateTime.UtcNow - cfg.LastRunUtc.Value.ToUniversalTime() < SuppressionWindow) continue;

                if (!_inFlight.TryAdd(key, 1))
                {
                    _log.LogWarning("Skipping auto sync of {Endpoint}: previous run still in flight", key);
                    continue;
                }
                try
                {
                    _log.LogInformation("Auto sync triggered for {Endpoint} at hour {Hour}", key, nowLocal.Hour);
                    var (ok, rows, msg) = await sync.SyncAsync(key, "auto-scheduler", "Auto", ct);
                    if (ok) _log.LogInformation("Auto sync {Endpoint} OK -- {Rows} rows. {Msg}", key, rows, msg);
                    else    _log.LogWarning("Auto sync {Endpoint} FAILED: {Msg}", key, msg);
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Auto sync of {Endpoint} threw", key);
            }
        }
    }
}
