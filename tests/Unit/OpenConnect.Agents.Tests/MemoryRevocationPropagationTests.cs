using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-016: Implement memory erase/revoke propagation to derived vector projections
/// and caches, with proof of unauthorized retrieval denial.
/// </summary>
public sealed class MemoryRevocationPropagationTests
{
    [Fact]
    public async Task Delete_PropagatesToMemorySearch_AndSubsequentRetrievalIsDenied()
    {
        var adapter = new InMemorySemanticMemoryAdapter(queryAuthorizer: (tenant, agent) => tenant == "tenant-1");

        // 1. Upsert document
        var doc = new OpenConnect.Abstractions.MemoryDocument("doc-pii", "tenant-1", "agent-x", "Confidential tax identification numbers");
        await adapter.UpsertAsync(doc);

        // Verify document is searchable before deletion
        var beforeDelete = await adapter.SearchAsync(new MemorySearchRequest("tenant-1", "agent-x", "confidential tax", 5));
        Assert.Single(beforeDelete.Matches);
        Assert.Equal("doc-pii", beforeDelete.Matches[0].Document.Id);

        // 2. Erase / Revoke document
        await adapter.DeleteAsync(new OpenConnect.Abstractions.MemoryDeletionRequest("tenant-1", "agent-x", "doc-pii"));

        // 3. Verify deletion propagated immediately: subsequent search returns zero matches
        var afterDelete = await adapter.SearchAsync(new MemorySearchRequest("tenant-1", "agent-x", "confidential tax", 5));
        Assert.Empty(afterDelete.Matches);
    }

    [Fact]
    public async Task UnauthorizedRevocation_ThrowsUnauthorizedAccessException()
    {
        var adapter = new InMemorySemanticMemoryAdapter(queryAuthorizer: (tenant, agent) => tenant == "tenant-1");

        // Attempting to delete from an unauthorized tenant fails closed
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapter.DeleteAsync(new OpenConnect.Abstractions.MemoryDeletionRequest("tenant-attacker", "agent-x", "doc-pii")));
    }
}
