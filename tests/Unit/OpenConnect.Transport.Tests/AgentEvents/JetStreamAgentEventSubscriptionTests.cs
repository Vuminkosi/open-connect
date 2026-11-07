using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Transport.Nats;
using OpenConnect.Transport.Nats.AgentEvents;
using Xunit;

namespace OpenConnect.Transport.Tests.AgentEvents;

public sealed class JetStreamAgentEventSubscriptionTests
{
    private static readonly CommittedAgentEvent Event = new(Guid.NewGuid(), "tenant", "session", "execution", "agent",
        AgentExecutionMode.Operational, "task.completed", 1, "projection:1", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Filters_and_acks_only_after_consumer_advances()
    {
        var broker = new FakeJetStream(new Delivery(Event));
        var subscription = new JetStreamAgentEventSubscription(broker);
        await using var enumerator = subscription.SubscribeAsync(new("consumer", ["task.completed"])).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(0, broker.Delivery.AckCount);
        Assert.Equal("consumer", broker.ConsumerName);
        Assert.Equal(new[] { "task.completed" }, broker.Types);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(1, broker.Delivery.AckCount);
        Assert.Equal(0, broker.Delivery.NakCount);
    }

    [Fact]
    public async Task Disposed_or_failed_processing_naks_for_redelivery()
    {
        var delivery = new Delivery(Event);
        var subscription = new JetStreamAgentEventSubscription(new FakeJetStream(delivery));
        await using (var enumerator = subscription.SubscribeAsync(new("consumer", ["task.completed"])).GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
            // Disposing before asking for another item means downstream processing did not complete.
        }
        Assert.Equal(1, delivery.NakCount);
        Assert.Equal(0, delivery.AckCount);
    }

    [Fact]
    public async Task Outbox_publisher_uses_event_id_for_deduplication()
    {
        var broker = new PublishBroker();
        await new OutboxEventPublisher(broker).PublishAsync(Event);
        Assert.Equal(OutboxEventPublisher.Subject, broker.Subject);
        Assert.Equal(Event.EventId.ToString("D"), broker.MessageId);
        Assert.Equal(Event, JsonSerializer.Deserialize<CommittedAgentEvent>(broker.Payload!));
    }

    private sealed class FakeJetStream(Delivery delivery) : IAgentEventJetStream
    {
        public Delivery Delivery => delivery;
        public string? ConsumerName { get; private set; }
        public IReadOnlyCollection<string>? Types { get; private set; }
        public async IAsyncEnumerable<IAgentEventDelivery> FetchAsync(string consumerName, IReadOnlyCollection<string> eventTypes,
            int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ConsumerName = consumerName;
            Types = eventTypes;
            yield return delivery;
            await Task.CompletedTask;
        }
    }

    private sealed class Delivery(CommittedAgentEvent value) : IAgentEventDelivery
    {
        public ReadOnlyMemory<byte> Data { get; } = JsonSerializer.SerializeToUtf8Bytes(value);
        public int AckCount { get; private set; }
        public int NakCount { get; private set; }
        public ValueTask AckAsync(CancellationToken cancellationToken) { AckCount++; return ValueTask.CompletedTask; }
        public ValueTask NakAsync(CancellationToken cancellationToken) { NakCount++; return ValueTask.CompletedTask; }
    }

    private sealed class PublishBroker : IJetStreamPublisher
    {
        public string? Subject { get; private set; }
        public string? MessageId { get; private set; }
        public byte[]? Payload { get; private set; }
        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            Subject = subject; MessageId = messageId; Payload = payload.ToArray();
            return ValueTask.CompletedTask;
        }
    }
}
