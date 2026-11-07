using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Adapter seam for the existing outbox relay. The callback must ACK only after durable broker ACK.</summary>
public interface INavigatorOutboxRelay
{
    Task<int> RelayOnceAsync(int limit, Guid leaseId, CancellationToken cancellationToken);
}

/// <summary>Bounded periodic relay; transient failures delay the next pass and do not stop the host.</summary>
public sealed class NavigatorOutboxRelayWorker(
    INavigatorOutboxRelay relay,
    ILogger<NavigatorOutboxRelayWorker> logger) : BackgroundService
{
    private const int BatchLimit = 100;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var count = await relay.RelayOnceAsync(BatchLimit, Guid.NewGuid(), stoppingToken).ConfigureAwait(false);
                delay = count == BatchLimit ? TimeSpan.Zero : IdleDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Navigator outbox relay pass failed; pending entries will be retried.");
                delay = FailureDelay;
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
}
