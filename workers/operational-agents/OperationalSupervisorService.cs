using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.WorkerHosts.Operational;

/// <summary>
/// Owns the durable supervisor lease for operational runs. The first host to acquire
/// <c>oc/operational/supervisor</c> supervises; any duplicate supervisor fails startup
/// instead of fencing the same executions twice. The lease is renewed on a heartbeat
/// and released on graceful shutdown; losing it stops the host fail-closed.
/// </summary>
public sealed class OperationalSupervisorService(
    IServiceScopeFactory scopes,
    IOptions<OperationalWorkerOptions> options,
    ILogger<OperationalSupervisorService> logger) : BackgroundService
{
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (config.Mode != OperationalAgentHostingMode.Standalone)
            throw new InvalidOperationException("operational_embedded_mode: this host only supervises in Standalone mode; Embedded delegates supervision to the gateway.");
        var tenant = new TenantId(Abstractions.Ulid.Parse(config.TenantUlid));

        long fencing;
        using (var scope = scopes.CreateScope())
        {
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseStore>();
            var acquired = await leases.TryAcquireAsync(
                new LeaseRecord(OperationalWorkerOptions.SupervisorResourceKey, tenant, _instanceId, DateTimeOffset.UtcNow + config.SupervisorLeaseDuration, 1),
                stoppingToken).ConfigureAwait(false);
            if (!acquired)
                throw new InvalidOperationException("operational_duplicate_supervisor: the supervisor lease is already held; refusing to start a second supervisor.");
            var current = await leases.GetAsync(OperationalWorkerOptions.SupervisorResourceKey, tenant, stoppingToken).ConfigureAwait(false);
            fencing = current?.FencingVersion ?? 1;
        }

        logger.LogInformation("Operational supervisor started (instance {InstanceId}, fencing {FencingVersion}).", _instanceId, fencing);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(config.RenewInterval, stoppingToken).ConfigureAwait(false);
            using var scope = scopes.CreateScope();
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseStore>();
            var renewed = await leases.RenewAsync(
                OperationalWorkerOptions.SupervisorResourceKey, tenant, _instanceId, fencing,
                DateTimeOffset.UtcNow + config.SupervisorLeaseDuration, stoppingToken).ConfigureAwait(false);
            if (!renewed)
                throw new InvalidOperationException("operational_supervisor_lease_lost: supervisor lease was taken over; stopping fail-closed.");
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var config = options.Value;
            var tenant = new TenantId(Abstractions.Ulid.Parse(config.TenantUlid));
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseStore>();
            await leases.ReleaseAsync(OperationalWorkerOptions.SupervisorResourceKey, tenant, _instanceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Operational supervisor lease release failed for instance {InstanceId}; the lease expires on its own.", _instanceId);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
