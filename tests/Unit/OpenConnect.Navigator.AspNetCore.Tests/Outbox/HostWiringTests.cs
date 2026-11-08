using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.DurableStore;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Transport.Nats;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class HostWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedEventIsRelayedAndOnlyAcknowledgedAfterBrokerSuccess(bool brokerFails)
    {
        var source = new OutboxSource();
        var broker = new FakeJetStreamPublisher { Fail = brokerFails };
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<IDurableOperationalStore, InMemoryDurableOperationalStore>();
        services.AddSingleton(source);
        services.AddSingleton<INavigatorOutboxWriter>(source);
        services.AddSingleton<IDurableNavigatorStore, InMemoryDurableNavigatorStore>();
        services.AddNavigatorOutboxRelay();
        services.AddCommittingNavigatorStore();

        using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IDurableNavigatorStore>();
        var session = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        var evt = new NavigatorEvent(Guid.NewGuid().ToString("D"), "command.completed", session,
            "task", 1, null, DateTimeOffset.UtcNow, new Dictionary<string, string>());

        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(session, "tenant", "principal", 1, evt));
        Assert.Single(source.Pending);

        var relay = new OutboxRelay(source, new NatsPublisher(broker));
        if (brokerFails)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => relay.RelayOnceAsync("navigator.events", 10, Guid.NewGuid()));
            Assert.Single(source.Pending);
            Assert.Empty(source.Delivered);
        }
        else
        {
            Assert.Equal(1, await relay.RelayOnceAsync("navigator.events", 10, Guid.NewGuid()));
            Assert.Empty(source.Pending);
            Assert.Single(source.Delivered);
            Assert.Single(broker.MessageIds);
        }
    }

    private sealed class FakeJetStreamPublisher : IJetStreamPublisher
    {
        public bool Fail { get; init; }
        public List<string> MessageIds { get; } = [];
        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("simulated broker failure");
            MessageIds.Add(messageId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OutboxSource : INavigatorOutboxWriter, IOutboxRelaySource
    {
        public List<OutboxEntry> Pending { get; } = [];
        public List<Guid> Delivered { get; } = [];
        public ValueTask WriteAsync(string topic, string messageId, string payload, CancellationToken cancellationToken = default)
        {
            Pending.Add(new OutboxEntry(Guid.Parse(messageId), topic, payload));
            return ValueTask.CompletedTask;
        }
        public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutboxEntry>>(Pending.Take(limit).ToArray());
        public Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken)
        {
            Delivered.Add(entryId);
            Pending.RemoveAll(entry => entry.Id == entryId);
            return Task.CompletedTask;
        }
    }
}
