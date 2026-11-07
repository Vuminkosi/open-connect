using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Transport.Nats.AgentEvents;

/// <summary>Broker delivery metadata and explicit settlement for a v2 pull-consumer message.</summary>
public interface IAgentEventDeliveryV2
{
    ReadOnlyMemory<byte> Data { get; }
    bool Redelivered { get; }
    ValueTask AckAsync(CancellationToken cancellationToken);
    ValueTask NakAsync(string? reason, CancellationToken cancellationToken);
}

/// <summary>Broker boundary for durable v2 JetStream pull consumption.</summary>
public interface IAgentEventJetStreamV2
{
    IAsyncEnumerable<IAgentEventDeliveryV2> FetchAsync(
        string consumerName,
        IReadOnlyCollection<string> eventTypes,
        int batchSize,
        CancellationToken cancellationToken);
}

/// <summary>Durable pull subscription with caller-controlled ACK and NAK settlement.</summary>
public sealed class JetStreamAgentEventSubscriptionV2(
    IAgentEventJetStreamV2 jetStream,
    int batchSize = 32) : IAgentEventSubscriptionV2
{
    private static readonly string[] DefaultEventTypes =
        ["task.completed", "agent.memory.candidate", "agent.execution.summarized"];

    public async IAsyncEnumerable<CommittedEventDelivery> SubscribeAsync(
        AgentEventSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerName);
        if (batchSize < 1) throw new InvalidOperationException("Batch size must be positive.");
        var types = options.EventTypes is { Length: > 0 } ? options.EventTypes : DefaultEventTypes;
        if (types.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Event types cannot be empty.", nameof(options));

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
                await delivery.NakAsync("Invalid event payload or type.", CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var settled = 0;
            async ValueTask Ack(CancellationToken token)
            {
                if (Interlocked.CompareExchange(ref settled, 1, 0) == 0)
                    await delivery.AckAsync(token).ConfigureAwait(false);
            }

            async ValueTask Nak(string? reason, CancellationToken token)
            {
                if (Interlocked.CompareExchange(ref settled, 1, 0) == 0)
                    await delivery.NakAsync(reason, token).ConfigureAwait(false);
            }

            yield return new CommittedEventDelivery(value, Ack, Nak, delivery.Redelivered);
        }
    }
}
