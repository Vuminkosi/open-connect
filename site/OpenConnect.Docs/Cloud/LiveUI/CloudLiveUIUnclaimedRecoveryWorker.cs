namespace OpenConnect.Docs.Cloud;

public interface ICloudLiveUIUnclaimedRecoveryStore
{
    Task<int> ReleaseAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken);
}

/// <summary>Periodically releases expired LiveUI reservations that never acquired a claim.</summary>
public sealed class CloudLiveUIUnclaimedRecoveryWorker(IServiceScopeFactory scopeFactory, TimeProvider timeProvider,
    ILogger<CloudLiveUIUnclaimedRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ICloudLiveUIUnclaimedRecoveryStore>()
                    .ReleaseAsync(timeProvider.GetUtcNow().AddMinutes(-5), 100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Cloud LiveUI unclaimed recovery failed; reservations remain held."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
