using FirstBlick.Infrastructure;

namespace FirstBlick.Worker;

public sealed class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSchedule = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextSchedule)
                {
                    await using var schedulerScope = scopes.CreateAsyncScope();
                    await schedulerScope.ServiceProvider.GetRequiredService<OutboxProcessor>().ScheduleDueNotifications(stoppingToken);
                    nextSchedule = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                await using var scope = scopes.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessNext(stoppingToken);
                if (!processed) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning("Outbox polling is temporarily unavailable; retrying without exposing job data");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
