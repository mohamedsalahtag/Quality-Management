// Background tick that walks every active rule once an hour. Each rule's
// schedule (Daily / EveryN / Once) plus the today-rule override decide
// whether AlertService.RunAlertAsync actually dispatches.

namespace YourApp.Services;

public class AlertScheduler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AlertScheduler> _logger;
    private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

    public AlertScheduler(IServiceProvider services, ILogger<AlertScheduler> logger)
    {
        _services = services; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so DB initializer + seeding finish first.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IAlertService>();
                var fired = await svc.RunAllDueAsync();
                if (fired > 0)
                    _logger.LogInformation("AlertScheduler tick — {Count} rule(s) fired", fired);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AlertScheduler tick failed");
            }
            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
