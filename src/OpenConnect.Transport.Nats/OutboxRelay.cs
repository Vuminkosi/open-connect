using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Transport.Nats;

/// <summary>Legacy relay source contract; it does not provide durable retry or lease-fencing semantics.</summary>
public interface IOutboxRelaySource
{
    Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken);
    Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken);
}

/// <summary>Compatibility adapter; IOutboxStore alone cannot persist retries or validate lease fencing. Durable hosts use the EF dispatcher instead.</summary>
public sealed class OutboxStoreRelaySource(IOutboxStore store) : IOutboxRelaySource
{
    public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken) => store.GetPendingAsync(limit, cancellationToken);
    public Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken) => store.MarkDeliveredAsync(entryId, cancellationToken);
}

public sealed class OutboxRelay(IOutboxRelaySource source, NatsPublisher publisher)
{
    public async Task<int> RelayOnceAsync(string subject, int limit, Guid leaseId, CancellationToken cancellationToken = default)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (leaseId == Guid.Empty)
            throw new ArgumentException("Lease id is required.", nameof(leaseId));
        var entries = await source.GetPendingAsync(limit, cancellationToken).ConfigureAwait(false);
        var delivered = 0;
        Exception? firstFailure = null;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var envelope = WorkerEnvelope.Parse(Encoding.UTF8.GetBytes(entry.Payload));
                if (envelope.MessageId != entry.Id)
                    throw new InvalidDataException("Outbox entry id must match envelope message id.");
                await publisher.PublishAsync(subject, Encoding.UTF8.GetBytes(entry.Payload), cancellationToken).ConfigureAwait(false);
                // Mark only after successful broker acknowledgement; exceptions leave the entry pending.
                await source.MarkDeliveredAsync(entry.Id, leaseId, cancellationToken).ConfigureAwait(false);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Legacy sources cannot persist retry state. Process independent
                // entries before propagating failure to the caller.
                firstFailure ??= exception;
            }
        }
        if (firstFailure is not null)
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        return delivered;
    }
}
