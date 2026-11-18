using System.Collections.Concurrent;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>An in-memory semantic memory adapter providing query-time tenant/agent authorization with zero external or Qdrant dependencies.</summary>
public sealed class InMemorySemanticMemoryAdapter : IAgentSemanticMemory
{
    private sealed record StoredDocument(Abstractions.MemoryDocument Doc, HashSet<string> Tokens);
    private readonly ConcurrentDictionary<(string TenantId, string AgentId, string DocumentId), StoredDocument> _documents = new();
    private readonly Func<string, string, bool>? _queryAuthorizer;

    /// <summary>Initializes the adapter with an optional query authorizer gate.</summary>
    /// <param name="queryAuthorizer">Optional predicate (tenantId, agentId) => isAuthorized.</param>
    public InMemorySemanticMemoryAdapter(Func<string, string, bool>? queryAuthorizer = null)
    {
        _queryAuthorizer = queryAuthorizer;
    }

    /// <inheritdoc />
    public Task<MemorySearchResult> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        if (request.Limit <= 0) throw new ArgumentOutOfRangeException(nameof(request.Limit));
        cancellationToken.ThrowIfCancellationRequested();

        // Query-time authorization gate
        if (_queryAuthorizer is not null && !_queryAuthorizer(request.TenantId, request.AgentId))
            throw new UnauthorizedAccessException($"Query-time authorization failed for tenant '{request.TenantId}' and agent '{request.AgentId}'.");

        var queryTokens = Tokenize(request.Query);
        var matches = new List<MemorySearchMatch>();

        foreach (var (key, stored) in _documents)
        {
            if (!string.Equals(key.TenantId, request.TenantId, StringComparison.Ordinal) ||
                !string.Equals(key.AgentId, request.AgentId, StringComparison.Ordinal))
            {
                continue;
            }

            var overlap = stored.Tokens.Intersect(queryTokens).Count();
            if (overlap > 0)
            {
                var score = (double)overlap / Math.Max(queryTokens.Count, stored.Tokens.Count);
                matches.Add(new MemorySearchMatch(stored.Doc, score));
            }
        }

        var results = matches.OrderByDescending(m => m.Score).Take(request.Limit).ToArray();
        return Task.FromResult(new MemorySearchResult(results));
    }

    /// <inheritdoc />
    public Task UpsertAsync(Abstractions.MemoryDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        ArgumentNullException.ThrowIfNull(document.Content);
        cancellationToken.ThrowIfCancellationRequested();

        if (_queryAuthorizer is not null && !_queryAuthorizer(document.TenantId, document.AgentId))
            throw new UnauthorizedAccessException($"Write authorization failed for tenant '{document.TenantId}'.");

        var key = (document.TenantId, document.AgentId, document.Id);
        var stored = new StoredDocument(document, Tokenize(document.Content));
        _documents[key] = stored;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(Abstractions.MemoryDeletionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DocumentId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_queryAuthorizer is not null && !_queryAuthorizer(request.TenantId, request.AgentId))
            throw new UnauthorizedAccessException($"Delete authorization failed for tenant '{request.TenantId}'.");

        var key = (request.TenantId, request.AgentId, request.DocumentId);
        _documents.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private static HashSet<string> Tokenize(string text)
    {
        return text.Split([' ', '\t', '\r', '\n', '.', ',', ';', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }
}
