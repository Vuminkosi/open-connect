using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>A session-scoped memory item with revision and lineage metadata.</summary>
public sealed record MemoryDocument(string Scope, string MemoryId, long Revision, string DataClass,
    string Summary, string Provenance, DateTimeOffset? ExpiresAt, bool Tombstoned);

/// <summary>Identifies a memory item within an authority scope.</summary>
public sealed record MemoryLookup(string Scope, string MemoryId);

/// <summary>Requests a policy-governed memory write.</summary>
/// <remarks><see cref="ConsentToken"/> is a caller-asserted approval marker for session-scoped notes, not an authorization boundary. Host policy must authorize memory and tool access before calling the store.</remarks>
public sealed record MemoryWriteRequest(string Scope, string MemoryId, string Summary, string DataClass,
    string Provenance, TimeSpan? Ttl, AgentMemoryWritePolicy WritePolicy = AgentMemoryWritePolicy.DenyByDefault,
    string? ConsentToken = null);

/// <summary>Requests permanent logical deletion of a memory item.</summary>
public sealed record MemoryDeletionRequest(string Scope, string MemoryId, string Reason);

/// <summary>Stores memory only within the supplied session scope. Durable consent and cross-session purging are owned elsewhere.</summary>
public interface IAgentMemoryStore
{
    /// <summary>Gets a memory item when its scope matches the lookup scope.</summary>
    ValueTask<MemoryDocument?> GetAsync(MemoryLookup lookup, CancellationToken cancellationToken = default);
    /// <summary>Creates or revises a memory item after write-policy enforcement.</summary>
    ValueTask<MemoryDocument> UpsertAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
    /// <summary>Creates an authoritative tombstone for the scoped memory item.</summary>
    ValueTask DeleteAsync(MemoryDeletionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Lock-protected, process-local session memory implementation.</summary>
/// <remarks>This store's consent-token presence check is not an authorization boundary. The host must enforce memory and tool authorization before invoking the store.</remarks>
public sealed class InMemorySessionMemoryStore : IAgentMemoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Scope, string MemoryId), MemoryDocument> _documents = new();
    private readonly HashSet<string> _allowedDataClasses;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a store. ApplicationPolicy defaults to the session-only data class.</summary>
    /// <param name="allowedDataClasses">Data classes permitted under ApplicationPolicy; defaults to <c>session</c>.</param>
    /// <param name="timeProvider">Clock used for deterministic expiry.</param>
    public InMemorySessionMemoryStore(IEnumerable<string>? allowedDataClasses = null, TimeProvider? timeProvider = null)
    {
        _allowedDataClasses = new HashSet<string>(allowedDataClasses ?? ["session"], StringComparer.Ordinal);
        if (_allowedDataClasses.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Data classes must be non-empty.", nameof(allowedDataClasses));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ValueTask<MemoryDocument?> GetAsync(MemoryLookup lookup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        Validate(lookup.Scope, lookup.MemoryId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_documents.Keys.Any(key => key.MemoryId == lookup.MemoryId && key.Scope != lookup.Scope))
                throw new UnauthorizedAccessException("Memory lookup scope does not match its stored scope.");
            if (!_documents.TryGetValue((lookup.Scope, lookup.MemoryId), out var document) || document.Tombstoned)
                return ValueTask.FromResult<MemoryDocument?>(null);
            if (document.ExpiresAt is { } expiry && expiry <= _timeProvider.GetUtcNow())
            {
                _documents[(lookup.Scope, lookup.MemoryId)] = document with { Revision = checked(document.Revision + 1), Tombstoned = true, Summary = "expired" };
                return ValueTask.FromResult<MemoryDocument?>(null);
            }
            return ValueTask.FromResult<MemoryDocument?>(document);
        }
    }

    /// <inheritdoc />
    public ValueTask<MemoryDocument> UpsertAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Scope, request.MemoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DataClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Provenance);
        if (request.Ttl is { } ttl && ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(request), "TTL must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        var consent = !string.IsNullOrWhiteSpace(request.ConsentToken);
        var permitted = request.WritePolicy switch
        {
            AgentMemoryWritePolicy.DenyByDefault => consent,
            AgentMemoryWritePolicy.ExplicitApproved => consent,
            AgentMemoryWritePolicy.ApplicationPolicy => _allowedDataClasses.Contains(request.DataClass),
            _ => false
        };
        if (!permitted)
            throw new UnauthorizedAccessException("Memory write policy denied the request.");

        lock (_gate)
        {
            var key = (request.Scope, request.MemoryId);
            var old = _documents.GetValueOrDefault(key);
            if (old?.Tombstoned == true) throw new InvalidOperationException("A tombstoned memory item cannot be resurrected.");
            var doc = new MemoryDocument(request.Scope, request.MemoryId, checked((old?.Revision ?? 0) + 1), request.DataClass,
                request.Summary, request.Provenance, request.Ttl is { } duration ? _timeProvider.GetUtcNow() + duration : null, false);
            _documents[key] = doc;
            return ValueTask.FromResult(doc);
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(MemoryDeletionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Scope, request.MemoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (request.Scope, request.MemoryId);
            var old = _documents.GetValueOrDefault(key);
            _documents[key] = new MemoryDocument(request.Scope, request.MemoryId, checked((old?.Revision ?? 0) + 1), old?.DataClass ?? "session",
                request.Reason, old?.Provenance ?? "deletion", old?.ExpiresAt, true);
        }
        return ValueTask.CompletedTask;
    }

    private static void Validate(string scope, string memoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
    }
}
