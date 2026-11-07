namespace OpenConnect.Abstractions;

/// <summary>Provides semantic search and lifecycle operations for agent memory.</summary>
public interface IAgentSemanticMemory
{
    /// <summary>Searches memory documents matching the request.</summary>
    Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a memory document.</summary>
    Task UpsertAsync(MemoryDocument document, CancellationToken cancellationToken = default);

    /// <summary>Deletes memory documents matching the request scope.</summary>
    Task DeleteAsync(MemoryDeletionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Criteria for a scoped semantic memory search.</summary>
public sealed record MemorySearchRequest(string TenantId, string AgentId, string Query, int Limit);

/// <summary>Results returned from a semantic memory search.</summary>
public sealed record MemorySearchResult(IReadOnlyList<MemorySearchMatch> Matches);

/// <summary>A memory document and its relevance score.</summary>
public sealed record MemorySearchMatch(MemoryDocument Document, double Score);

/// <summary>A tenant- and agent-scoped memory document.</summary>
public sealed record MemoryDocument(string Id, string TenantId, string AgentId, string Content);

/// <summary>Scope used to remove memory documents.</summary>
public sealed record MemoryDeletionRequest(string TenantId, string AgentId, string DocumentId);
