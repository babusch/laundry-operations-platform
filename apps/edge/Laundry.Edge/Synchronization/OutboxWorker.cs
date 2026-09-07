namespace Laundry.Edge.Synchronization;

public sealed class OutboxWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    IHostEnvironment environment, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("Forwarding:Enabled")) return;
        var settings = ForwardingSettings.Read(configuration);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var dispatched = await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>()
                    .DispatchOneAsync(settings, stoppingToken);
                if (dispatched) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // Keep local acceptance alive; do not log exception text that may contain data.
                logger.LogWarning("Outbox iteration failed; retained work will be retried. Check plant storage and migrations.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
