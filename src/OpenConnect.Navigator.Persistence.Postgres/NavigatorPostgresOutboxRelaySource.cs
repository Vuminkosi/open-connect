using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Navigator.Operational;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Transport.Nats;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>
/// Reads Navigator messages from the shared outbox. Delivery is at-least-once: the relay
/// records delivery only after broker acknowledgement; consumers deduplicate by event ID.
/// The relay lease ID is accepted by the shared contract but is not persisted or validated.
/// Tenant filtering is intentionally performed by the authorized projection, not this relay.
/// </summary>
public sealed class NavigatorPostgresOutboxRelaySource(OpenConnectDbContext db) : IOutboxRelaySource
{
    public async Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var rows = await db.Outbox.AsNoTracking()
            .Where(entry => entry.Topic.StartsWith("navigator.") && entry.State == "pending" && entry.NextAttemptAt <= DateTimeOffset.UtcNow)
            .OrderBy(entry => entry.NextAttemptAt)
            .ThenBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .Take(limit)
            .Select(entry => new { entry.Id, entry.Topic, entry.Payload })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(entry => new OutboxEntry(entry.Id, entry.Topic, entry.Payload)).ToArray();
    }

    public async Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken)
    {
        // leaseId is required by IOutboxRelaySource, but schema and approved semantics
        // deliberately do not provide relay lease fencing. Lease-based exclusion is deferred.
        _ = leaseId;
        await db.Outbox
            .Where(entry => entry.Id == entryId && entry.Topic.StartsWith("navigator.") && entry.State == "pending")
            .ExecuteUpdateAsync(update => update
                .SetProperty(entry => entry.State, "delivered")
                .SetProperty(entry => entry.DeliveredAt, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
    }
}
