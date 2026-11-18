using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Transport.Nats;

namespace OpenConnect.WorkerHosts.Outbox;

/// <summary>The shipped host's durable, per-entry fenced dispatch path.</summary>
public sealed class OutboxBatchDispatcher(OpenConnectDbContext db, NatsPublisher publisher,
    IOptions<OutboxWorkerOptions> options, ILogger<OutboxBatchDispatcher> logger, IServiceScopeFactory scopes)
{
    /// <summary>Dispatches due entries; failures are deferred or quarantined independently.</summary>
    public Task<int> DispatchAsync(string leaseOwner, CancellationToken cancellationToken = default) =>
        DispatchCoreAsync(leaseOwner, null, cancellationToken);

    private Task<int> DispatchCoreAsync(string leaseOwner, Func<CancellationToken, Task>? validateSupervisor, CancellationToken cancellationToken)
    {
        var config = options.Value;
        return new EfOutboxDispatcher().ExecuteBatchAsync(db,
            new EnvelopePublisher(publisher, config.Subject, logger, validateSupervisor), leaseOwner,
            config.SupervisorLeaseDuration, config.BatchSize, config.MaxAttempts, cancellationToken);
    }

    /// <summary>Maintains the supervisor lease in independent scopes and cancels dispatch on ownership loss.</summary>
    public async Task<int> DispatchUnderSupervisorAsync(string leaseOwner, long fencingVersion, CancellationToken cancellationToken = default)
    {
        var config = options.Value;
        var tenant = new TenantId(Abstractions.Ulid.Parse(config.TenantUlid));
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(batch.Token);
        batch.CancelAfter(config.SupervisorLeaseDuration);
        var heartbeat = Task.CompletedTask;

        async Task RenewAsync(CancellationToken ct)
        {
            // Never share the dispatcher's DbContext with a concurrent heartbeat.
            using var scope = scopes.CreateScope();
            var leases = scope.ServiceProvider.GetRequiredService<ILeaseStore>();
            var current = await leases.GetAsync(OutboxWorkerOptions.SupervisorResourceKey, tenant, ct).ConfigureAwait(false);
            if (current is null || current.Owner != leaseOwner || current.FencingVersion != fencingVersion || current.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new OutboxSupervisorLeaseLostException();
            // Retain the old expiry deadline until renewal is durably confirmed.
            var remaining = current.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new OutboxSupervisorLeaseLostException();
            batch.CancelAfter(remaining);
            var expiresAt = DateTimeOffset.UtcNow + config.SupervisorLeaseDuration;
            if (!await leases.RenewAsync(OutboxWorkerOptions.SupervisorResourceKey, tenant,
                    leaseOwner, fencingVersion, expiresAt, ct).ConfigureAwait(false))
                throw new OutboxSupervisorLeaseLostException();
            current = await leases.GetAsync(OutboxWorkerOptions.SupervisorResourceKey, tenant, ct).ConfigureAwait(false);
            if (current is null || current.Owner != leaseOwner || current.FencingVersion != fencingVersion || current.ExpiresAt < expiresAt)
                throw new OutboxSupervisorLeaseLostException();
            remaining = current.ExpiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new OutboxSupervisorLeaseLostException();
            ct.ThrowIfCancellationRequested();
            batch.CancelAfter(remaining);
        }

        async Task ValidateBeforePublishAsync(CancellationToken ct)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var current = await scope.ServiceProvider.GetRequiredService<ILeaseStore>()
                    .GetAsync(OutboxWorkerOptions.SupervisorResourceKey, tenant, ct).ConfigureAwait(false);
                if (current is null || current.Owner != leaseOwner || current.FencingVersion != fencingVersion || current.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new OutboxSupervisorLeaseLostException();
            }
            catch
            {
                batch.Cancel();
                batch.Token.ThrowIfCancellationRequested();
                throw;
            }
        }

        async Task HeartbeatAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(config.SupervisorLeaseDuration / 3, heartbeatStop.Token).ConfigureAwait(false);
                    await RenewAsync(heartbeatStop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
            catch
            {
                // Do not expose provider errors, connection strings, or payloads.
                batch.Cancel();
            }
        }

        try
        {
            try
            {
                await RenewAsync(batch.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                batch.Cancel();
                throw new OutboxSupervisorLeaseLostException();
            }
            heartbeat = HeartbeatAsync();
            var delivered = await DispatchCoreAsync(leaseOwner, ValidateBeforePublishAsync, batch.Token).ConfigureAwait(false);
            batch.Token.ThrowIfCancellationRequested();
            return delivered;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && batch.IsCancellationRequested)
        {
            throw new OutboxSupervisorLeaseLostException();
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat.ConfigureAwait(false);
        }
    }

    private sealed class EnvelopePublisher(NatsPublisher publisher, string subject,
        ILogger logger, Func<CancellationToken, Task>? validateSupervisor) : ICommittedOutboxPublisher
    {
        public async Task PublishAsync(string storedSubject, string eventId, string sanitizedPayload, CancellationToken ct)
        {
            try
            {
                if (validateSupervisor is not null)
                    await validateSupervisor(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes(sanitizedPayload);
                var envelope = WorkerEnvelope.Parse(bytes);
                if (!Guid.TryParse(eventId, out var id) || envelope.MessageId != id)
                    throw new InvalidDataException("Outbox event id must match envelope message id.");
                await publisher.PublishAsync(subject, bytes, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Exception messages and payloads are untrusted and can contain secrets.
                logger.LogWarning("Outbox entry publish failed ({ErrorType}); durable retry policy applies.", exception.GetType().Name);
                throw;
            }
        }
    }
}

internal sealed class OutboxSupervisorLeaseLostException() : InvalidOperationException(
    "outbox_supervisor_lease_lost: dispatcher ownership could not be maintained; stopping fail-closed.");
