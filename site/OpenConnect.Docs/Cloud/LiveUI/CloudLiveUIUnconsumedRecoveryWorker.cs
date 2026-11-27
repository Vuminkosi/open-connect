namespace OpenConnect.Docs.Cloud;

public interface ICloudLiveUIUnconsumedRecoveryStore
{
    Task<int> ReleaseAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken);
}

/// <summary>Periodically releases expired LiveUI claims that have never been consumed.</summary>
public sealed class CloudLiveUIUnconsumedRecoveryWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider,
    ILogger<CloudLiveUIUnconsumedRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICloudLiveUIUnconsumedRecoveryStore>()
                    .ReleaseAsync(timeProvider.GetUtcNow().AddMinutes(-5), 100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Cloud LiveUI claim recovery failed; reservations remain held."); }
            try
            { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
