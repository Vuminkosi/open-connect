using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.WorkerHosts.Outbox;

/// <summary>
/// Relays committed outbox entries to JetStream. Ordering per batch: acquire/renew the
/// durable supervisor lease (fences replicas, single active dispatcher), publish each
/// envelope and await PUBACK, then CAS-mark the entry delivered. No database transaction
/// is ever held across the broker call: every store access is a short independent
/// <c>SaveChanges</c>, and JetStream message-id deduplication (envelope message id)
/// makes redelivery after a crash idempotent.
/// </summary>
public sealed class OutboxDispatcherService(
    IServiceScopeFactory scopes,
    NatsJetStreamPublisher publisher,
    IOptions<OutboxWorkerOptions> options,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (config.Mode != OutboxHostingMode.Standalone)
            throw new InvalidOperationException("outbox_embedded_mode: this host only dispatches in Standalone mode; Embedded delegates dispatch to the gateway.");
        var tenant = new TenantId(Abstractions.Ulid.Parse(config.TenantUlid));

        await publisher.ConnectAsync(stoppingToken).ConfigureAwait(false);

        long fencing;
        using (var scope = scopes.CreateScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseStore>();
            var acquired = await leases.TryAcquireAsync(
                new LeaseRecord(OutboxWorkerOptions.SupervisorResourceKey, tenant, _instanceId, DateTimeOffset.UtcNow + config.SupervisorLeaseDuration, 1),
                stoppingToken).ConfigureAwait(false);
            if (!acquired)
                throw new InvalidOperationException("outbox_duplicate_supervisor: the dispatcher lease is already held; refusing to start a second dispatcher.");
            var current = await leases.GetAsync(OutboxWorkerOptions.SupervisorResourceKey, tenant, stoppingToken).ConfigureAwait(false);
            if (current is null || current.Owner != _instanceId || current.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new OutboxSupervisorLeaseLostException();
            fencing = current.FencingVersion;
        }

        logger.LogInformation("Outbox dispatcher started (instance {InstanceId}, fencing {FencingVersion}, subject {Subject}).", _instanceId, fencing, config.Subject);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var provider = scope.ServiceProvider;
            try
            {
                var relay = provider.GetRequiredService<OutboxBatchDispatcher>();
                var delivered = await relay.DispatchUnderSupervisorAsync(_instanceId, fencing, stoppingToken).ConfigureAwait(false);
                if (delivered > 0)
                    logger.LogInformation("Outbox relay delivered {Delivered} entries.", delivered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OutboxSupervisorLeaseLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Entries stay pending and are redriven on the next poll; never crash the loop on a transient broker/store fault.
                logger.LogError("Outbox relay batch failed ({ErrorType}); unfinished entries remain pending for redrive.", ex.GetType().Name);
            }

            await Task.Delay(config.PollingInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel/await the active batch and its heartbeat before releasing authority.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // BackgroundService may stop waiting when the shutdown deadline expires.
        // A cancellation-ignoring broker must not outlive released authority.
        if (ExecuteTask is not { IsCompleted: true })
        {
            logger.LogWarning("Outbox dispatcher has not finished stopping; supervisor lease will expire without an early release.");
            return;
        }
        try
        {
            using var scope = scopes.CreateScope();
            var config = options.Value;
            var tenant = new TenantId(Abstractions.Ulid.Parse(config.TenantUlid));
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseStore>();
            await leases.ReleaseAsync(OutboxWorkerOptions.SupervisorResourceKey, tenant, _instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Outbox dispatcher lease release failed ({ErrorType}) for instance {InstanceId}; the lease expires on its own.", ex.GetType().Name, _instanceId);
        }
    }
}
