using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Transport.Nats.AgentEvents;

/// <summary>Publishes committed outbox events to the shared JetStream event stream.</summary>
public sealed class OutboxEventPublisher(IJetStreamPublisher publisher)
{
    public const string Subject = "oc.v1.execution.event";

    public ValueTask PublishAsync(CommittedAgentEvent committedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(committedEvent);
        if (committedEvent.EventId == Guid.Empty || string.IsNullOrWhiteSpace(committedEvent.EventType))
            throw new ArgumentException("A committed event requires an id and type.", nameof(committedEvent));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(committedEvent);
        return publisher.PublishAsync(Subject, bytes, committedEvent.EventId.ToString("D"), cancellationToken);
    }
}
