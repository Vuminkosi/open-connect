using OpenConnect.Abstractions;
using OpenConnect.Observability;
using System.Diagnostics;

namespace OpenConnect.QdrantVactor;

/// <summary>Tenant- and agent-scoped semantic memory provider over the configured vector store.</summary>
public sealed class SemanticMemorySearchProvider(IEmbeddingGenerator embeddings, IVectorStore store, IAgentScopeAuthorizer authorizer) : IAgentSemanticMemory
{
    public async Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScope(request.TenantId, request.AgentId);
        if (!authorizer.Authorize(request.TenantId, request.AgentId))
            throw new UnauthorizedAccessException("The requested tenant and agent scope is not authorized.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        if (request.Limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request.Limit));
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        using var activity = Instrumentation.SafeStartActivity("vectors", "search", [new("operation", "search"), new("component", "qdrantvactor")]);
        try
        {
            var vector = await embeddings.GenerateAsync(request.Query, cancellationToken).ConfigureAwait(false);
            var points = await store.SearchAsync(request.TenantId, request.AgentId, vector, request.Limit, cancellationToken).ConfigureAwait(false);
            // Enforce tenant/agent again at the boundary, independently of backend filtering.
            var matches = points.Where(point => point.TenantId == request.TenantId && point.AgentId == request.AgentId)
                .Select(point => new MemorySearchMatch(new MemoryDocument(point.Id, point.TenantId, point.AgentId, point.Content), 0))
                .ToArray();
            return new MemorySearchResult(matches);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch
        {
            outcome = "failure";
            throw;
        }
        finally
        {
            Instrumentation.SafeRecordVector(Stopwatch.GetElapsedTime(started).TotalMilliseconds, "search", outcome);
        }
    }

    public async Task UpsertAsync(MemoryDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateScope(document.TenantId, document.AgentId);
        if (!authorizer.Authorize(document.TenantId, document.AgentId))
            throw new UnauthorizedAccessException("The requested tenant and agent scope is not authorized.");
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Content);
        var vector = await embeddings.GenerateAsync(document.Content, cancellationToken).ConfigureAwait(false);
        await store.UpsertAsync(new VectorPoint(document.Id, document.TenantId, document.AgentId, document.Content, vector, 0), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(MemoryDeletionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateScope(request.TenantId, request.AgentId);
        if (!authorizer.Authorize(request.TenantId, request.AgentId))
            throw new UnauthorizedAccessException("The requested tenant and agent scope is not authorized.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentId);
        return store.DeleteAsync(request.TenantId, request.AgentId, request.DocumentId, cancellationToken);
    }

    private static void ValidateScope(string tenantId, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
    }
}
