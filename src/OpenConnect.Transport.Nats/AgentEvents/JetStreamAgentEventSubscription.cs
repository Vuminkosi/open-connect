using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Transport.Nats.AgentEvents;

/// <summary>A received JetStream message with explicit settlement operations.</summary>
public interface IAgentEventDelivery
{
    ReadOnlyMemory<byte> Data { get; }
    ValueTask AckAsync(CancellationToken cancellationToken);
    ValueTask NakAsync(CancellationToken cancellationToken);
}

/// <summary>Broker boundary implemented by the JetStream pull-consumer adapter.</summary>
public interface IAgentEventJetStream
{
    IAsyncEnumerable<IAgentEventDelivery> FetchAsync(string consumerName, IReadOnlyCollection<string> eventTypes, int batchSize, CancellationToken cancellationToken);
}

/// <summary>Durable, at-least-once event subscription. Advancing the stream settles the prior item as successful.</summary>
public sealed class JetStreamAgentEventSubscription(IAgentEventJetStream jetStream, int batchSize = 32) : IAgentEventSubscription
{
    private static readonly string[] DefaultEventTypes = ["task.completed", "agent.memory.candidate", "agent.execution.summarized"];

    public async IAsyncEnumerable<CommittedAgentEvent> SubscribeAsync(
        AgentEventSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerName);
        if (batchSize < 1) throw new InvalidOperationException("Batch size must be positive.");
        var types = options.EventTypes is { Length: > 0 } ? options.EventTypes : DefaultEventTypes;
        if (types.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Event types cannot be empty.", nameof(options));

        await foreach (var delivery in jetStream.FetchAsync(options.ConsumerName, types, batchSize, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            CommittedAgentEvent value;
            try
            {
                value = JsonSerializer.Deserialize<CommittedAgentEvent>(delivery.Data.Span)
                    ?? throw new JsonException("Event payload is empty.");
                if (value.EventId == Guid.Empty || string.IsNullOrWhiteSpace(value.EventType))
                    throw new JsonException("Event identity and type are required.");
                if (!types.Contains(value.EventType, StringComparer.Ordinal))
                {
                    await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch
            {
                await delivery.NakAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            // Consumer completion is represented by requesting the next item. If it fails or stops
            // enumeration, finally NAKs so JetStream redelivers this event (at-least-once semantics).
            var settled = false;
            try
            {
                yield return value;
                await delivery.AckAsync(cancellationToken).ConfigureAwait(false);
                settled = true;
            }
            finally
            {
                if (!settled) await delivery.NakAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
