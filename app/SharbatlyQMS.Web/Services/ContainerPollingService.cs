using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Periodically fetches every SAP container row whose document date
/// (TOC_DATE) is on/after the configured start date and UPSERTs them into
/// <c>qms_sap_container_cache</c>. The /Arrivals/Pending page consumes
/// that cache; this service exists so the page doesn't have to hammer
/// SAP on every render and so newly-arrived containers surface without
/// a human searching first.
///
/// Configuration (Admin → Settings → SAP):
///   Container.PreCollectedStartDate   ISO date (yyyy-MM-dd); required.
///   Container.PollingIntervalMinutes  integer minutes; defaults to 60.
///
/// Resilience: every run is logged into <c>qms_sap_sync_log</c> under
/// endpoint_key = 'ContainerCache' so the next tick's cursor survives
/// restarts. The same overlap guard idiom as <see cref="AutoSyncService"/>
/// prevents two concurrent polls.
/// </summary>
public class ContainerPollingService : BackgroundService
{
    public const string EndpointKey = ContainerCacheService.SyncLogEndpointKey;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int DefaultIntervalMinutes      = 60;

    private readonly IServiceProvider _services;
    private readonly ILogger<ContainerPollingService> _log;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    public ContainerPollingService(IServiceProvider services, ILogger<ContainerPollingService> log)
    {
        _services = services; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One-shot startup cleanup: any sync_log row left in-flight from a
        // previous host crash / deploy interruption is marked failed so the
        // "A pull is already running" guard doesn't stay stuck forever.
        // Mirrors the cleanup_stale_sync.sql pattern used by AutoSyncService.
        try { await SweepStaleAsync(stoppingToken); }
        catch (Exception ex) { _log.LogWarning(ex, "Container startup sweep failed"); }

        // Initial settle so the host has finished priming everything.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "Container polling tick failed"); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepStaleAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var cs = config.GetConnectionString("Default")!;
        using var c = new SqlConnection(cs);
        var rows = await c.ExecuteAsync(@"
            UPDATE qms_sap_sync_log
            SET    completed_at = SYSUTCDATETIME(),
                   success      = 0,
                   message      = COALESCE(message, '') + ' [startup sweep: previous run did not complete]'
            WHERE  endpoint_key = @EndpointKey AND completed_at IS NULL",
            new { EndpointKey });
        if (rows > 0)
            _log.LogInformation("Container startup sweep: marked {Rows} stale in-flight sync_log row(s) as cancelled.", rows);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var cache    = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();
        var config   = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var cfg = await settings.GetContainerPollConfigAsync();
        if (cfg.StartDate is null) return;

        var intervalMinutes = cfg.PollingMinutes > 0 ? cfg.PollingMinutes : DefaultIntervalMinutes;

        // Cursor: when did the last successful pull finish?
        var cs = config.GetConnectionString("Default")!;
        DateTime? lastCompletedUtc;
        using (var c = new SqlConnection(cs))
        {
            lastCompletedUtc = await c.ExecuteScalarAsync<DateTime?>(@"
                SELECT TOP 1 completed_at
                FROM   qms_sap_sync_log
                WHERE  endpoint_key = @EndpointKey AND success = 1
                ORDER  BY completed_at DESC",
                new { EndpointKey });
        }
        if (lastCompletedUtc.HasValue &&
            DateTime.UtcNow - lastCompletedUtc.Value < TimeSpan.FromMinutes(intervalMinutes))
            return; // not due yet

        if (!_inFlight.TryAdd(EndpointKey, 1))
        {
            _log.LogWarning("Skipping container pull: previous run still in flight");
            return;
        }
        try
        {
            await cache.RefreshFromSapAsync(cfg.StartDate.Value, "auto-scheduler", "Auto", ct);
        }
        catch (Exception ex)
        {
            // RefreshFromSapAsync already logged + wrote sync_log; swallow so the loop continues.
            _log.LogError(ex, "Container pull tick failed (already logged)");
        }
        finally
        {
            _inFlight.TryRemove(EndpointKey, out _);
        }
    }
}
