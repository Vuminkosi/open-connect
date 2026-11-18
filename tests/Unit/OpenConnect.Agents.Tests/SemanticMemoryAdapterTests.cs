using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-015: Implement optional semantic-memory retrieval adapter
/// with query-time authorization and no core Qdrant dependency.
/// </summary>
public sealed class SemanticMemoryAdapterTests
{
    [Fact]
    public async Task Search_AuthorizedQuery_ReturnsMatches()
    {
        var adapter = new InMemorySemanticMemoryAdapter(queryAuthorizer: (tenant, agent) => tenant == "tenant-good");

        await adapter.UpsertAsync(new OpenConnect.Abstractions.MemoryDocument("doc-1", "tenant-good", "agent-1", "Machine learning optimization strategies"));
        await adapter.UpsertAsync(new OpenConnect.Abstractions.MemoryDocument("doc-2", "tenant-good", "agent-1", "Database indexing and performance"));

        var result = await adapter.SearchAsync(new MemorySearchRequest("tenant-good", "agent-1", "machine learning", 10));

        Assert.Single(result.Matches);
        Assert.Equal("doc-1", result.Matches[0].Document.Id);
        Assert.True(result.Matches[0].Score > 0);
    }

    [Fact]
    public async Task Search_UnauthorizedQuery_ThrowsUnauthorizedAccessException()
    {
        var adapter = new InMemorySemanticMemoryAdapter(queryAuthorizer: (tenant, agent) => tenant == "tenant-good");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.SearchAsync(new MemorySearchRequest("tenant-bad", "agent-1", "query", 5)));
    }

    [Fact]
    public void OpenConnectAgentsAssembly_HasNoDependencyOnQdrant()
    {
        var agentsAssembly = typeof(InMemorySemanticMemoryAdapter).Assembly;
        var referencedAssemblies = agentsAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Qdrant", StringComparison.OrdinalIgnoreCase) == true);
    }
}
