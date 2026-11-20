using OpenConnect.Abstractions;
using OpenConnect.QdrantVactor;
using Xunit;

namespace OpenConnect.QdrantVactor.Tests;

public sealed class SemanticMemoryScoreTests
{
    [Fact]
    public async Task SearchPreservesScoresAndScopeAndRanksMissingScoresAsZero()
    {
        var store = new ScoreStore([
            new VectorPoint("low", "tenant", "agent", "low", new float[] { 1, 0 }, 0, Score: 0.4),
            new VectorPoint("other", "foreign", "agent", "private", new float[] { 1, 0 }, 0, Score: 1),
            new VectorPoint("high", "tenant", "agent", "high", new float[] { 1, 0 }, 0, Score: 0.9),
            new VectorPoint("missing", "tenant", "agent", "missing", new float[] { 1, 0 }, 0)
        ]);
        var provider = new SemanticMemorySearchProvider(new Embeddings(), store, new AllowScope());

        var result = await provider.SearchAsync(new MemorySearchRequest("tenant", "agent", "query", 10));

        Assert.Equal(new[] { "high", "low", "missing" }, result.Matches.Select(match => match.Document.Id));
        Assert.Equal(new[] { 0.9, 0.4, 0d }, result.Matches.Select(match => match.Score));
    }

    private sealed class Embeddings : IEmbeddingGenerator
    {
        public ValueTask<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ReadOnlyMemory<float>>(new float[] { 1, 0 });
    }

    private sealed class AllowScope : IAgentScopeAuthorizer
    {
        public bool Authorize(string tenantId, string agentId) => true;
    }

    private sealed class ScoreStore(IReadOnlyList<VectorPoint> points) : IVectorStore
    {
        public Task<IReadOnlyList<VectorPoint>> SearchAsync(string tenantId, string agentId, ReadOnlyMemory<float> vector, int limit, CancellationToken cancellationToken) => Task.FromResult(points);
        public Task UpsertAsync(VectorPoint point, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string tenantId, string agentId, string documentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
