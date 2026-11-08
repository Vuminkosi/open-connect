using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class CommittingStoreTests
{
    [Fact]
    public async Task SuccessfulAppendForwardsCommittedEventOnce_WhenDecoratorIsRegisteredFirst()
    {
        var inner = new InMemoryDurableNavigatorStore(new InMemoryDurableOperationalStore());
        var hook = new SpyHook();
        var provider = new StoreProvider();
        var decorator = new CommittingDurableNavigatorStore(provider, hook);
        provider.Stores = [decorator, inner];
        var id = await decorator.CreateSessionAsync("tenant", "principal", "client", "route");
        var value = new NavigatorEvent("event-1", "command.completed", id, "task-1", 1, null, DateTimeOffset.UtcNow, new Dictionary<string, string>());

        Assert.Equal(NavigatorStoreOutcome.Succeeded, await decorator.AppendEventAsync(id, "tenant", "principal", 1, value));
        Assert.Equal(id, hook.SessionId);
        Assert.Equal(value, Assert.Single(Assert.Single(hook.Batches)));
    }

    [Theory]
    [InlineData(NavigatorStoreOutcome.Conflict)]
    [InlineData(NavigatorStoreOutcome.NotFound)]
    public async Task NonSuccessfulAppendDoesNotForward(NavigatorStoreOutcome outcome)
    {
        var inner = new InMemoryDurableNavigatorStore(new InMemoryDurableOperationalStore());
        var id = await inner.CreateSessionAsync("tenant", "principal", "client", "route");
        var hook = new SpyHook();
        var provider = new StoreProvider();
        var decorator = new CommittingDurableNavigatorStore(provider, hook);
        provider.Stores = [inner, decorator];
        var value = new NavigatorEvent("event-1", "command.completed", id, "task-1", 2, null, DateTimeOffset.UtcNow, new Dictionary<string, string>());
        var actual = outcome == NavigatorStoreOutcome.NotFound
            ? await decorator.AppendEventAsync("missing", "tenant", "principal", 2, value)
            : await decorator.AppendEventAsync(id, "tenant", "principal", 2, value);
        Assert.Equal(outcome, actual);
        Assert.Empty(hook.Batches);
    }

    [Fact]
    public async Task MissingInnerFailsClosed()
    {
        var provider = new StoreProvider();
        var decorator = new CommittingDurableNavigatorStore(provider, new SpyHook());
        provider.Stores = [decorator];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => decorator.CreateSessionAsync("t", "p", "c", "r").AsTask());
        Assert.Contains("exactly one other IDurableNavigatorStore", error.Message);
    }

    private sealed class StoreProvider : IServiceProvider
    {
        public IEnumerable<IDurableNavigatorStore> Stores { get; set; } = [];
        public object? GetService(Type serviceType) => serviceType == typeof(IEnumerable<IDurableNavigatorStore>) ? Stores : null;
    }

    private sealed class SpyHook : INavigatorCommitHook
    {
        public string? SessionId { get; private set; }
        public List<IReadOnlyList<NavigatorEvent>> Batches { get; } = [];
        public Task AfterCommitAsync(string sessionId, IReadOnlyList<NavigatorEvent> committedEvents, CancellationToken cancellationToken = default)
        {
            SessionId = sessionId;
            Batches.Add(committedEvents);
            return Task.CompletedTask;
        }
    }
}
