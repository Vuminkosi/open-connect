using System.Collections.Concurrent;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>An application-level conversation turn event with provenance, tracked separately from raw MAF transcripts.</summary>
/// <param name="EventId">Unique identifier of this application event.</param>
/// <param name="EventType">Dotted event type name (e.g. conversation.turn.completed).</param>
/// <param name="SessionId">Owning session identifier.</param>
/// <param name="TenantScope">Tenant authority scope.</param>
/// <param name="TurnSequence">Monotonic turn sequence number.</param>
/// <param name="ActorId">Actor or agent initiating or completing the turn.</param>
/// <param name="OccurredAt">Timestamp when the event occurred.</param>
/// <param name="Provenance">Authoritative origin or source audit description.</param>
/// <param name="Summary">Sanitized app-level turn summary, never raw model scratchpad bytes.</param>
public sealed record ConversationTurnEvent(
    Guid EventId,
    string EventType,
    SessionId SessionId,
    string TenantScope,
    long TurnSequence,
    string ActorId,
    DateTimeOffset OccurredAt,
    string Provenance,
    string Summary);

/// <summary>Stores application conversation and turn event history independently from raw MAF transcript state.</summary>
public interface IConversationEventHistory
{
    /// <summary>Appends an application turn event after validating tenant and sequence invariants.</summary>
    ValueTask AppendEventAsync(ConversationTurnEvent turnEvent, CancellationToken cancellationToken = default);

    /// <summary>Retrieves application turn events for an authorized session scope.</summary>
    ValueTask<IReadOnlyList<ConversationTurnEvent>> GetEventsAsync(
        SessionId sessionId,
        string tenantScope,
        CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory store for application conversation events, isolated by tenant and session.</summary>
public sealed class InMemoryConversationEventHistory : IConversationEventHistory
{
    private readonly ConcurrentDictionary<(string TenantScope, SessionId SessionId), List<ConversationTurnEvent>> _events = new();
    private readonly object _gate = new();

    /// <inheritdoc />
    public ValueTask AppendEventAsync(ConversationTurnEvent turnEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turnEvent);
        if (turnEvent.EventId == Guid.Empty) throw new ArgumentException("EventId must be non-empty.", nameof(turnEvent));
        ArgumentException.ThrowIfNullOrWhiteSpace(turnEvent.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnEvent.TenantScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnEvent.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(turnEvent.Provenance);
        if (turnEvent.TurnSequence < 0) throw new ArgumentOutOfRangeException(nameof(turnEvent), "TurnSequence must be non-negative.");
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = (turnEvent.TenantScope, turnEvent.SessionId);
            if (!_events.TryGetValue(key, out var list))
            {
                list = [];
                _events[key] = list;
            }

            if (list.Count > 0 && turnEvent.TurnSequence <= list[^1].TurnSequence)
                throw new InvalidOperationException($"Turn sequence {turnEvent.TurnSequence} is not strictly monotonic; prior was {list[^1].TurnSequence}.");

            list.Add(turnEvent);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConversationTurnEvent>> GetEventsAsync(
        SessionId sessionId,
        string tenantScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantScope);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var key = (tenantScope, sessionId);
            if (_events.TryGetValue(key, out var list))
            {
                return ValueTask.FromResult<IReadOnlyList<ConversationTurnEvent>>(list.ToArray());
            }

            return ValueTask.FromResult<IReadOnlyList<ConversationTurnEvent>>([]);
        }
    }
}
