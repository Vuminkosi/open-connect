using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Transport.Nats;

/// <summary>Relay-facing outbox contract. Lease fencing must be added to the persistence store in a follow-up.</summary>
public interface IOutboxRelaySource
{
    Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken);
    Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken);
}

/// <summary>Temporary adapter; existing IOutboxStore does not persist or validate lease fencing.</summary>
public sealed class OutboxStoreRelaySource(IOutboxStore store) : IOutboxRelaySource
{
    public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken) => store.GetPendingAsync(limit, cancellationToken);
    public Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken) => store.MarkDeliveredAsync(entryId, cancellationToken);
}

public sealed class OutboxRelay(IOutboxRelaySource source, NatsPublisher publisher)
{
    public async Task<int> RelayOnceAsync(string subject, int limit, Guid leaseId, CancellationToken cancellationToken = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (leaseId == Guid.Empty) throw new ArgumentException("Lease id is required.", nameof(leaseId));
        var entries = await source.GetPendingAsync(limit, cancellationToken).ConfigureAwait(false);
        var delivered = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = WorkerEnvelope.Parse(Encoding.UTF8.GetBytes(entry.Payload));
            if (envelope.MessageId != entry.Id) throw new InvalidDataException("Outbox entry id must match envelope message id.");
            await publisher.PublishAsync(subject, Encoding.UTF8.GetBytes(entry.Payload), cancellationToken).ConfigureAwait(false);
            // Mark only after successful broker acknowledgement; exceptions leave the entry pending.
            await source.MarkDeliveredAsync(entry.Id, leaseId, cancellationToken).ConfigureAwait(false);
            delivered++;
        }
        return delivered;
    }
}
