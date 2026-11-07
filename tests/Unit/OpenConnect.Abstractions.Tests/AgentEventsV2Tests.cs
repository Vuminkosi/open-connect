using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Abstractions.Tests;

public sealed class AgentEventsV2Tests
{
    [Fact]
    public async Task Subscribe_delivery_can_be_acknowledged_without_nak()
    {
        var ackCalls = 0;
        var nakCalls = 0;
        var delivery = CreateDelivery(
            _ =>
            {
                ackCalls++;
                return ValueTask.CompletedTask;
            },
            (_, _) =>
            {
                nakCalls++;
                return ValueTask.CompletedTask;
            });
        IAgentEventSubscriptionV2 subscription = new FakeAgentEventSubscriptionV2(delivery);

        await foreach (var received in subscription.SubscribeAsync(
            new AgentEventSubscriptionOptions("projection", ["completed"])))
            await received.AckAsync(CancellationToken.None);

        Assert.Equal(1, ackCalls);
        Assert.Equal(0, nakCalls);
    }

    [Fact]
    public async Task Subscribe_delivery_can_be_rejected_without_ack()
    {
        var ackCalls = 0;
        var nakCalls = 0;
        var delivery = CreateDelivery(
            _ =>
            {
                ackCalls++;
                return ValueTask.CompletedTask;
            },
            (_, _) =>
            {
                nakCalls++;
                return ValueTask.CompletedTask;
            });
        IAgentEventSubscriptionV2 subscription = new FakeAgentEventSubscriptionV2(delivery);

        await foreach (var received in subscription.SubscribeAsync(
            new AgentEventSubscriptionOptions("projection", ["completed"])))
            await received.NakAsync("processing failed", CancellationToken.None);

        Assert.Equal(0, ackCalls);
        Assert.Equal(1, nakCalls);
    }

    private static CommittedEventDelivery CreateDelivery(
        Func<CancellationToken, ValueTask> ack,
        Func<string?, CancellationToken, ValueTask> nak)
    {
        var committedEvent = new CommittedAgentEvent(
            Guid.NewGuid(), "tenant", "session", "execution", "agent",
            AgentExecutionMode.Interactive, "completed", 1, "projection-reference", DateTimeOffset.UtcNow);

        return new CommittedEventDelivery(committedEvent, ack, nak, Redelivered: false);
    }

    private sealed class FakeAgentEventSubscriptionV2(CommittedEventDelivery delivery) : IAgentEventSubscriptionV2
    {
        public async IAsyncEnumerable<CommittedEventDelivery> SubscribeAsync(
            AgentEventSubscriptionOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return delivery;
            await Task.CompletedTask;
        }
    }
}
