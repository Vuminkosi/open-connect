using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational.Durable;

namespace OpenConnect.Samples.Navigator;

/// <summary>
/// Result of one outbox-relay pass: relayed safe events, skipped unsafe events, and the
/// highest event sequence observed (use as the next pass's <c>afterSequence</c> cursor).
/// </summary>
/// <param name="Relayed">Number of events enqueued into the EFCore outbox.</param>
/// <param name="Skipped">Number of events rejected by the privacy-safety gate (never relayed).</param>
/// <param name="LastSequence">Highest sequence observed, inclusive.</param>
public sealed record NavigatorRelayResult(int Relayed, int Skipped, long LastSequence);

/// <summary>
/// Bridges the Navigator event log (<see cref="IDurableNavigatorStore"/>) to the EFCore
/// outbox (<see cref="IOutboxStore"/>). Only privacy-safe events
/// (<see cref="NavigatorEventTypes.IsSafe"/>) are relayed; unsafe events are counted as
/// skipped and never touch the outbox payload. Relayed entries carry safe metadata only
/// (event/session identity, sequence, timestamps); no raw DOM, media, or personal data.
/// </summary>
/// <remarks>
/// Lease-scoped relay gating (holding a fencing lease while relaying) is intentionally not
/// wired here: the lease demo and the relay demo exercise the two adapters independently
/// so each verb stays deterministic. Receipt paths are out of scope (carrier UNDECIDED).
/// </remarks>
public sealed class NavigatorOutboxRelay(IDurableNavigatorStore navigatorStore, IOutboxStore outboxStore)
{
    private readonly IDurableNavigatorStore _navigatorStore = navigatorStore ?? throw new ArgumentNullException(nameof(navigatorStore));
    private readonly IOutboxStore _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));

    /// <summary>
    /// Relays events after <paramref name="afterSequence"/> into the EFCore outbox.
    /// Re-running with the returned <c>LastSequence</c> relays zero further events
    /// (idempotent cursor). Contains no raw SQL; store interfaces only.
    /// </summary>
    public async Task<NavigatorRelayResult> RelayAsync(
        string sessionId, string tenant, string principal, long afterSequence, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);

        var events = await _navigatorStore.GetEventsAsync(sessionId, tenant, principal, afterSequence, cancellationToken).ConfigureAwait(false);

        var relayed = 0;
        var skipped = 0;
        var last = afterSequence;
        foreach (var evt in events.OrderBy(e => e.Sequence))
        {
            if (evt.Sequence <= afterSequence)
            {
                continue;
            }

            last = Math.Max(last, evt.Sequence);
            if (!NavigatorEventTypes.IsSafe(evt))
            {
                skipped++;
                continue;
            }

            var payload = JsonSerializer.Serialize(new
            {
                eventId = evt.EventId,
                sessionId = evt.SessionId,
                type = evt.Type,
                sequence = evt.Sequence,
                occurredAt = evt.OccurredAt,
                taskId = evt.TaskId,
                correlationId = evt.CorrelationId,
                metadata = evt.Metadata,
            });
            await _outboxStore.EnqueueAsync(new OutboxEntry(Guid.NewGuid(), evt.Type, payload), cancellationToken).ConfigureAwait(false);
            relayed++;
        }

        return new NavigatorRelayResult(relayed, skipped, last);
    }
}
