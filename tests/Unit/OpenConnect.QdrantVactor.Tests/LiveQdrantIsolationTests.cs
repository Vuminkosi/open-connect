using System.Net.Http.Json;
using OpenConnect.QdrantVactor;
using Xunit;

namespace OpenConnect.QdrantVactor.Tests;

public sealed class LiveQdrantIsolationTests
{
    [LiveQdrantFact]
    [Trait("Category", "QdrantLive")]
    public async Task SameLogicalIdRemainsIndependentAcrossScopesInQdrant()
    {
        var endpoint = new Uri(Environment.GetEnvironmentVariable("OPENCONNECT_QDRANT_TEST_ENDPOINT")!);
        if (endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)
            throw new InvalidOperationException("Live Qdrant tests require a loopback HTTP endpoint.");
        var collection = "oc_isolation_" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient { BaseAddress = endpoint };
        http.DefaultRequestHeaders.Add("api-key", "openconnect-disposable-test-key");
        var created = false;
        try
        {
            using var create = await http.PutAsJsonAsync($"collections/{collection}", new { vectors = new { size = 2, distance = "Cosine" } });
            create.EnsureSuccessStatusCode();
            created = true;
            using var vectorHttp = new HttpClient();
            var store = new QdrantVectorStoreRest(vectorHttp, endpoint, "openconnect-disposable-test-key", collection, 2);
            var provider = new SemanticMemorySearchProvider(new Embeddings(), store, new Authorizer());
            await provider.UpsertAsync(new("shared", "tenant-a", "agent-a", "first"));
            await provider.UpsertAsync(new("shared", "tenant-b", "agent-a", "second"));
            await provider.UpsertAsync(new("shared", "tenant-a", "agent-b", "third"));
            await provider.UpsertAsync(new("shared", "tenant-a", "agent-a", "updated"));

            var first = Assert.Single((await provider.SearchAsync(new("tenant-a", "agent-a", "query", 10))).Matches).Document;
            Assert.Equal("shared", first.Id);
            Assert.Equal("updated", first.Content);
            Assert.Equal("second", Assert.Single((await provider.SearchAsync(new("tenant-b", "agent-a", "query", 10))).Matches).Document.Content);
            Assert.Equal("third", Assert.Single((await provider.SearchAsync(new("tenant-a", "agent-b", "query", 10))).Matches).Document.Content);
            await provider.DeleteAsync(new("tenant-a", "agent-a", "shared"));
            Assert.Empty((await provider.SearchAsync(new("tenant-a", "agent-a", "query", 10))).Matches);
            Assert.Single((await provider.SearchAsync(new("tenant-b", "agent-a", "query", 10))).Matches);
            Assert.Single((await provider.SearchAsync(new("tenant-a", "agent-b", "query", 10))).Matches);
        }
        finally
        {
            if (created)
            {
                using var delete = await http.DeleteAsync($"collections/{collection}");
                delete.EnsureSuccessStatusCode();
            }
        }
    }

    private sealed class Embeddings : IEmbeddingGenerator
    {
        public ValueTask<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ReadOnlyMemory<float>>(new float[] { 1, 0 });
    }

    private sealed class Authorizer : IAgentScopeAuthorizer
    {
        public bool Authorize(string tenantId, string agentId) => true;
    }

    private sealed class LiveQdrantFactAttribute : FactAttribute
    {
        public LiveQdrantFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_QDRANT_TEST_ENDPOINT")))
                Skip = "Set OPENCONNECT_QDRANT_TEST_ENDPOINT to a disposable loopback Qdrant instance.";
        }
    }
}
