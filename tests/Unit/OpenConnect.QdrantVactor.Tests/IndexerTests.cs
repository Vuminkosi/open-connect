using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Abstractions;
using OpenConnect.QdrantVactor;
using OpenConnect.Observability;
using System.Diagnostics;
using Xunit;

namespace OpenConnect.QdrantVactor.Tests;

public sealed class IndexerTests
{
    [Fact]
    public async Task SearchEmitsOnlySafeVectorActivityDimensions()
    {
        var tags = new List<KeyValuePair<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "OpenConnect.Vectors",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => tags.AddRange(activity.TagObjects)
        };
        ActivitySource.AddActivityListener(listener);

        var provider = new SemanticMemorySearchProvider(new DeterministicEmbeddingStub(), new CountingStore(), new FakeAuthorizer(true));
        await provider.SearchAsync(new("tenant-sensitive", "agent-sensitive", "private query", 10));

        Assert.Contains(tags, tag => tag.Key == "operation" && Equals(tag.Value, "search"));
        Assert.Contains(tags, tag => tag.Key == "component" && Equals(tag.Value, "qdrantvactor"));
        Assert.DoesNotContain(tags, tag => Equals(tag.Value, "tenant-sensitive") || Equals(tag.Value, "agent-sensitive") || Equals(tag.Value, "private query"));
    }

    [Fact]
    public void VectorDimensionsRejectUnboundedValuesAndKeys()
    {
        var tags = Instrumentation.AllowlistedTags([
            new("operation", "search"), new("component", "qdrantvactor"),
            new("outcome", "success"), new("tenant_id", "tenant-sensitive"),
            new("operation", "private query")
        ]);

        Assert.Equal(3, tags.Length);
        Assert.DoesNotContain(tags, tag => tag.Key == "tenant_id" || Equals(tag.Value, "private query"));
    }

    [Fact]
    public async Task DisabledIndexerDoesNotSubscribe()
    {
        var subscription = new FakeSubscription([]);
        var indexer = Create(subscription, new FakeReader("sanitized"), new FakeStore());
        await indexer.RunAsync();
        Assert.Equal(0, subscription.Calls);
    }

    [Fact]
    public void EnabledWithoutEndpointFailsValidation()
    {
        var result = new QdrantVactorOptionsValidator().Validate(null, new QdrantVactorOptions { Enabled = true });
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ApprovedEventUpsertsBeforeSubscriptionAdvancesAndUnapprovedIsIgnored()
    {
        var store = new FakeStore();
        var events = new[] { Event("task.completed", 1), Event("task.started", 2) };
        var subscription = new FakeSubscription(events, () => Assert.Single(store.Points));
        await Create(subscription, new FakeReader("safe output"), store, enabled: true).RunAsync();
        Assert.Single(store.Points);
        Assert.Equal("safe output", Assert.Single(store.Points).Content);
    }

    [Fact]
    public async Task DuplicateDeliveryProducesDeterministicSinglePoint()
    {
        var item = Event("agent.memory.candidate", 4);
        var store = new FakeStore();
        await Create(new FakeSubscription([item, item]), new FakeReader("safe"), store, enabled: true).RunAsync();
        Assert.Single(store.Points);
    }

    [Fact]
    public async Task SearchNeverReturnsPointsOwnedByAnotherTenant()
    {
        var store = new InMemoryVectorStore();
        var embedding = new DeterministicEmbeddingStub();
        await store.UpsertAsync(new VectorPoint("foreign", "tenant-b", "agent", "secret", await embedding.GenerateAsync("query", default), 1), default);
        await store.UpsertAsync(new VectorPoint("owned", "tenant-a", "agent", "owned", await embedding.GenerateAsync("query", default), 1), default);
        var result = await new SemanticMemorySearchProvider(embedding, store, new FakeAuthorizer(true)).SearchAsync(new("tenant-a", "agent", "query", 10));
        Assert.Equal("owned", Assert.Single(result.Matches).Document.Id);
    }

    [Fact]
    public async Task SearchDeniesUnauthorizedScopeBeforeStoreAccess()
    {
        var store = new CountingStore();
        var provider = new SemanticMemorySearchProvider(new DeterministicEmbeddingStub(), store, new FakeAuthorizer(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.SearchAsync(new("tenant-b", "agent", "query", 10)));
        Assert.Equal(0, store.SearchCalls);
    }

    [Fact]
    public async Task UpsertDeniesUnauthorizedScopeBeforeStoreAccess()
    {
        var store = new CountingStore();
        var provider = new SemanticMemorySearchProvider(new DeterministicEmbeddingStub(), store, new FakeAuthorizer(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.UpsertAsync(new("tenant-b", "agent", "doc", "content")));
        Assert.Equal(0, store.UpsertCalls);
    }

    [Fact]
    public async Task DeleteDeniesUnauthorizedScopeBeforeStoreAccess()
    {
        var store = new CountingStore();
        var provider = new SemanticMemorySearchProvider(new DeterministicEmbeddingStub(), store, new FakeAuthorizer(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.DeleteAsync(new("tenant-b", "agent", "doc")));
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task IndexerSkipsEventWithoutTrustedTenantScope()
    {
        var store = new FakeStore();
        var tampered = Event("task.completed", 1) with { TenantId = " " };
        await Create(new FakeSubscription([tampered]), new FakeReader("must not be indexed"), store, enabled: true).RunAsync();
        Assert.Empty(store.Points);
    }

    [Fact]
    public async Task DisabledPackageStartsWithoutEventSubscription()
    {
        using var host = new HostBuilder().ConfigureServices(collection =>
        {
            collection.AddLogging();
            collection.AddQdrantVactor(options => options.Enabled = false);
        }).Build();
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task StoreDeleteIsScopedToTenantAndAgent()
    {
        var store = new InMemoryVectorStore();
        var point = new VectorPoint("same", "tenant-a", "agent-a", "content", new float[] { 1 }, 1);
        await store.UpsertAsync(point, default);
        await store.UpsertAsync(point with { TenantId = "tenant-b" }, default);
        await store.DeleteAsync("tenant-a", "agent-a", "same", default);
        Assert.Single(await store.SearchAsync("tenant-b", "agent-a", new float[] { 1 }, 10, default));
    }

    private static QdrantVactorIndexer Create(FakeSubscription subscription, IProjectionReader reader, FakeStore store, bool enabled = false) =>
        new(reader, new DeterministicEmbeddingStub(), store,
            Options.Create(new QdrantVactorOptions { Enabled = enabled, Endpoint = enabled ? new Uri("http://localhost:6333") : null }), subscription);

    private static CommittedAgentEvent Event(string type, long sequence) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "tenant", "session", "execution", "agent", AgentExecutionMode.Interactive, type, sequence, "opaque-ref", DateTimeOffset.UtcNow);

    private sealed class FakeSubscription(IReadOnlyList<CommittedAgentEvent> events, Action? beforeNext = null) : IAgentEventSubscription
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<CommittedAgentEvent> SubscribeAsync(AgentEventSubscriptionOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            for (var i = 0; i < events.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return events[i];
                beforeNext?.Invoke();
                await Task.Yield();
            }
        }
    }

    private sealed class FakeReader(string? value) : IProjectionReader
    {
        public Task<string?> ReadSanitizedAsync(string projectionReference, string tenantId, CancellationToken cancellationToken) => Task.FromResult(value);
    }

    private sealed class FakeAuthorizer(bool allowed) : IAgentScopeAuthorizer
    {
        public bool Authorize(string tenantId, string agentId) => allowed;
    }

    private sealed class CountingStore : IVectorStore
    {
        public int SearchCalls { get; private set; }
        public int UpsertCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public Task UpsertAsync(VectorPoint point, CancellationToken cancellationToken) { UpsertCalls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<VectorPoint>> SearchAsync(string tenantId, string agentId, ReadOnlyMemory<float> vector, int limit, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return Task.FromResult<IReadOnlyList<VectorPoint>>([]);
        }
        public Task DeleteAsync(string tenantId, string agentId, string documentId, CancellationToken cancellationToken) { DeleteCalls++; return Task.CompletedTask; }
    }

    private sealed class FakeStore : IVectorStore
    {
        private readonly Dictionary<string, VectorPoint> _points = new(StringComparer.Ordinal);
        public IReadOnlyCollection<VectorPoint> Points => _points.Values;
        public Task UpsertAsync(VectorPoint point, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); _points[point.Id] = point; return Task.CompletedTask; }
        public Task<IReadOnlyList<VectorPoint>> SearchAsync(string tenantId, string agentId, ReadOnlyMemory<float> vector, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VectorPoint>>(_points.Values.Where(point => point.TenantId == tenantId && point.AgentId == agentId).Take(limit).ToArray());
        public Task DeleteAsync(string tenantId, string agentId, string documentId, CancellationToken cancellationToken) { _points.Remove(documentId); return Task.CompletedTask; }
    }
}
