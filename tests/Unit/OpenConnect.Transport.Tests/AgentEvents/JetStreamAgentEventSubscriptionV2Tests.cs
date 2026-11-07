using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Transport.Nats.AgentEvents;
using Xunit;

namespace OpenConnect.Transport.Tests.AgentEvents;

public sealed class JetStreamAgentEventSubscriptionV2Tests
{
    private static readonly CommittedAgentEvent Event = new(Guid.NewGuid(), "tenant", "session", "execution", "agent",
        AgentExecutionMode.Operational, "task.completed", 1, "projection:1", DateTimeOffset.UtcNow);

    [Fact]
    public async Task Uses_durable_consumer_event_filter_and_bounded_batch()
    {
        var broker = new FakeBroker(Event);
        var subscription = new JetStreamAgentEventSubscriptionV2(broker, 7);
        var delivery = await First(subscription);

        Assert.Equal(Event, delivery.Event);
        Assert.Equal("durable-projection", broker.ConsumerName);
        Assert.Equal(new[] { "task.completed" }, broker.EventTypes);
        Assert.Equal(7, broker.BatchSize);
    }

    [Fact]
    public async Task Ack_settles_delivery_and_does_not_redeliver()
    {
        var broker = new FakeBroker(Event);
        var delivery = await First(new JetStreamAgentEventSubscriptionV2(broker));

        await delivery.AckAsync(CancellationToken.None);
        await broker.DeliverPendingAsync();

        Assert.Equal(1, broker.AckCount);
        Assert.Equal(0, broker.NakCount);
        Assert.Empty(broker.Redeliveries);
    }

    [Fact]
    public async Task Nak_redelivers_same_event_with_redelivered_metadata()
    {
        var broker = new FakeBroker(Event);
        var subscription = new JetStreamAgentEventSubscriptionV2(broker);
        var first = await First(subscription);
        await first.NakAsync("retry", CancellationToken.None);

        await using var enumerator = subscription.SubscribeAsync(new("durable-projection", ["task.completed"])).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(Event.EventId, enumerator.Current.Event.EventId);
        Assert.True(enumerator.Current.Redelivered);
        Assert.Equal("retry", Assert.Single(broker.NakReasons));
    }

    private static async Task<CommittedEventDelivery> First(IAgentEventSubscriptionV2 subscription)
    {
        await using var enumerator = subscription.SubscribeAsync(new("durable-projection", ["task.completed"])).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private sealed class FakeBroker : IAgentEventJetStreamV2
    {
        private readonly Queue<FakeDelivery> _pending = new();
        private readonly Action _ackCallback;
        private readonly Action<FakeDelivery, string?> _nakCallback;
        public string? ConsumerName { get; private set; }
        public IReadOnlyCollection<string>? EventTypes { get; private set; }
        public int BatchSize { get; private set; }
        public int AckCount { get; private set; }
        public int NakCount { get; private set; }
        public List<string?> NakReasons { get; } = [];
        public List<Guid> Redeliveries { get; } = [];

        public FakeBroker(CommittedAgentEvent first) : this(first, true) { }

        private FakeBroker(CommittedAgentEvent first, bool initialize)
        {
            _ackCallback = () => AckCount++;
            _nakCallback = (delivery, reason) => Nak(delivery, reason);
            if (initialize) _pending.Enqueue(NewDelivery(first, false));
        }

        public async IAsyncEnumerable<IAgentEventDeliveryV2> FetchAsync(string consumerName,
            IReadOnlyCollection<string> eventTypes, int batchSize,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ConsumerName = consumerName;
            EventTypes = eventTypes.ToArray();
            BatchSize = batchSize;
            var remaining = Math.Min(batchSize, _pending.Count);
            for (var i = 0; i < remaining; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return _pending.Dequeue();
                await Task.Yield();
            }
        }

        public ValueTask DeliverPendingAsync() => ValueTask.CompletedTask;

        private void Ack() => AckCount++;
        private void Nak(FakeDelivery delivery, string? reason)
        {
            NakCount++;
            NakReasons.Add(reason);
            var redelivery = NewDelivery(delivery.Event, true);
            Redeliveries.Add(redelivery.Event.EventId);
            _pending.Enqueue(redelivery);
        }

        private FakeDelivery NewDelivery(CommittedAgentEvent value, bool redelivered) =>
            new(value, redelivered, _ackCallback, _nakCallback);
    }

    private sealed class FakeDelivery : IAgentEventDeliveryV2
    {
        private readonly Action? _ack;
        private readonly Action<FakeDelivery, string?>? _nak;
        public CommittedAgentEvent Event { get; }
        public ReadOnlyMemory<byte> Data { get; }
        public bool Redelivered { get; }

        public FakeDelivery(CommittedAgentEvent value, bool redelivered = false,
            Action? onAck = null, Action<FakeDelivery, string?>? onNak = null)
        {
            Event = value;
            Data = JsonSerializer.SerializeToUtf8Bytes(value);
            Redelivered = redelivered;
            _ack = onAck;
            _nak = onNak;
        }

        public ValueTask AckAsync(CancellationToken cancellationToken) { _ack?.Invoke(); return ValueTask.CompletedTask; }
        public ValueTask NakAsync(string? reason, CancellationToken cancellationToken)
        {
            _nak?.Invoke(this, reason);
            return ValueTask.CompletedTask;
        }
    }
}
