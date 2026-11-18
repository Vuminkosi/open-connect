using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Transport.Nats;

namespace OpenConnect.WorkerHosts.Outbox;

/// <summary>The shipped host's durable, per-entry fenced dispatch path.</summary>
public sealed class OutboxBatchDispatcher(OpenConnectDbContext db, NatsPublisher publisher,
    IOptions<OutboxWorkerOptions> options, ILogger<OutboxBatchDispatcher> logger)
{
    /// <summary>Dispatches due entries; failures are deferred or quarantined independently.</summary>
    public Task<int> DispatchAsync(string leaseOwner, CancellationToken cancellationToken = default)
    {
        var config = options.Value;
        return new EfOutboxDispatcher().ExecuteBatchAsync(db,
            new EnvelopePublisher(publisher, config.Subject, logger), leaseOwner,
            config.SupervisorLeaseDuration, config.BatchSize, config.MaxAttempts, cancellationToken);
    }

    private sealed class EnvelopePublisher(NatsPublisher publisher, string subject,
        ILogger logger) : ICommittedOutboxPublisher
    {
        public async Task PublishAsync(string storedSubject, string eventId, string sanitizedPayload, CancellationToken ct)
        {
            try
            {
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
