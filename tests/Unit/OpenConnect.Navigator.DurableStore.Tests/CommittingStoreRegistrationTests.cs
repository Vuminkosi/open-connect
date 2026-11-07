using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.DurableStore;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using Xunit;

namespace OpenConnect.Navigator.DurableStore.Tests;

public sealed class CommittingStoreRegistrationTests
{
    [Fact]
    public async Task SupportedRegistrationOrderForwardsSuccessfulAppendOnce()
    {
        var hook = new SpyHook();
        var services = new ServiceCollection();
        services.AddSingleton<InMemoryDurableOperationalStore>();
        services.AddScoped<IDurableNavigatorStore>(provider =>
            new InMemoryDurableNavigatorStore(provider.GetRequiredService<InMemoryDurableOperationalStore>()));
        services.AddSingleton<INavigatorCommitHook>(hook);
        services.AddCommittingNavigatorStore();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDurableNavigatorStore>();
        var id = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        var value = new NavigatorEvent("event-1", "command.completed", id, "task-1", 1, null,
            DateTimeOffset.UtcNow, new Dictionary<string, string>());

        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(id, "tenant", "principal", 1, value));
        Assert.Equal(id, hook.SessionId);
        Assert.Equal(value, Assert.Single(Assert.Single(hook.Batches)));
    }

    [Fact]
    public async Task WithoutDecoratorRawInnerIsResolvedAndHookIsNotCalled()
    {
        var hook = new SpyHook();
        var services = new ServiceCollection();
        services.AddSingleton<InMemoryDurableOperationalStore>();
        services.AddScoped<IDurableNavigatorStore>(provider =>
            new InMemoryDurableNavigatorStore(provider.GetRequiredService<InMemoryDurableOperationalStore>()));
        services.AddSingleton<INavigatorCommitHook>(hook);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDurableNavigatorStore>();
        Assert.IsType<InMemoryDurableNavigatorStore>(store);
        var id = await store.CreateSessionAsync("tenant", "principal", "client", "route");
        var value = new NavigatorEvent("event-1", "command.completed", id, "task-1", 1, null,
            DateTimeOffset.UtcNow, new Dictionary<string, string>());

        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(id, "tenant", "principal", 1, value));
        Assert.Empty(hook.Batches);
    }

    private sealed class SpyHook : INavigatorCommitHook
    {
        public string? SessionId { get; private set; }
        public List<IReadOnlyList<NavigatorEvent>> Batches { get; } = [];

        public Task AfterCommitAsync(string sessionId, IReadOnlyList<NavigatorEvent> committedEvents,
            CancellationToken cancellationToken = default)
        {
            SessionId = sessionId;
            Batches.Add(committedEvents);
            return Task.CompletedTask;
        }
    }
}
