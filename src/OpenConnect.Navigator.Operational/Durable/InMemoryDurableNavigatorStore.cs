using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Operational;

namespace OpenConnect.Navigator.Operational.Durable;

/// <summary>Process-local adapter for tests and development; operational leases are process-local and non-namespaced, so use only as a development adapter.</summary>
public sealed class InMemoryDurableNavigatorStore : IDurableNavigatorStore
{
    public InMemoryDurableNavigatorStore(IDurableOperationalStore operationalStore)
    {
        ArgumentNullException.ThrowIfNull(operationalStore);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, (NavigatorSession Session, string ClientInstance, string RouteId)> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Session, string Command), NavigatorApprovalRecord> _approvals = new();
    private readonly HashSet<(string Session, string Command)> _consumed = [];
    private readonly Dictionary<(string Tenant, string Principal, string Key), NavigatorReceiptRecord> _receipts = new();
    private readonly Dictionary<string, List<NavigatorEvent>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string ExecutionId), long> _observedFences = new();

    public ValueTask<NavigatorSession?> GetSessionAsync(string id, string tenant, string principal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_sessions.TryGetValue(id, out var session) && IsOwned(session.Session, tenant, principal) ? session.Session : null);
    }

    public ValueTask<NavigatorStoreOutcome> UpdateSessionSnapshotAsync(string id, string tenant, string principal, NavigatorSessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null) return ValueTask.FromResult(NavigatorStoreOutcome.Conflict);
        try { snapshot.Validate(); }
        catch (ArgumentException) { return ValueTask.FromResult(NavigatorStoreOutcome.Conflict); }
        lock (_gate)
        {
            if (!Owned(id, tenant, principal)) return ValueTask.FromResult(NavigatorStoreOutcome.NotFound);
            var current = _sessions[id];
            if (!StringComparer.Ordinal.Equals(snapshot.SessionId, id) || !StringComparer.Ordinal.Equals(snapshot.TenantId, tenant) || !StringComparer.Ordinal.Equals(snapshot.PrincipalId, principal))
                return ValueTask.FromResult(NavigatorStoreOutcome.Conflict);
            if (current.Session.Revision != expectedRevision || snapshot.Revision != expectedRevision + 1)
                return ValueTask.FromResult(NavigatorStoreOutcome.StaleFence);
            _sessions[id] = (NavigatorSession.Restore(snapshot), snapshot.ClientInstance, snapshot.RouteId);
            return ValueTask.FromResult(NavigatorStoreOutcome.Succeeded);
        }
    }

    public ValueTask<string> CreateSessionAsync(string tenant, string principal, string clientInstance, string routeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            _sessions.Add(id, (new NavigatorSession(tenant, principal), clientInstance, routeId));
            _events.Add(id, []);
            return ValueTask.FromResult(id);
        }
    }

    public ValueTask<NavigatorStoreOutcome> CreatePendingApprovalAsync(string id, string tenant, string principal, NavigatorApprovalRecord approval, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(approval);
        lock (_gate)
        {
            if (!Owned(id, tenant, principal)) return ValueTask.FromResult(NavigatorStoreOutcome.NotFound);
            var key = (id, approval.CommandId);
            return ValueTask.FromResult(_approvals.TryAdd(key, approval) ? NavigatorStoreOutcome.Succeeded : NavigatorStoreOutcome.Conflict);
        }
    }

    public ValueTask<NavigatorStoreResult> ConsumeApprovalAndSaveReceiptAsync(string id, string tenant, string principal, string commandId, string digest, DateTimeOffset now, NavigatorReceiptRecord receipt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(receipt);
        lock (_gate)
        {
            if (!Owned(id, tenant, principal)) return ValueTask.FromResult(new NavigatorStoreResult(NavigatorStoreOutcome.NotFound));
            var key = (id, commandId);
            var receiptKey = (tenant, principal, receipt.IdempotencyKey);
            if (_receipts.TryGetValue(receiptKey, out var previous))
            {
                // An expired idempotency key cannot replay, even when the retry payload matches.
                if (previous.ExpiresAt <= now) return ValueTask.FromResult(new NavigatorStoreResult(NavigatorStoreOutcome.Expired));
                var matches = ReceiptMatches(previous, receipt);
                return ValueTask.FromResult(new NavigatorStoreResult(matches ? NavigatorStoreOutcome.Succeeded : NavigatorStoreOutcome.Conflict, matches));
            }
            if (!_approvals.TryGetValue(key, out var approval)) return ValueTask.FromResult(new NavigatorStoreResult(_consumed.Contains(key) ? NavigatorStoreOutcome.AlreadyConsumed : NavigatorStoreOutcome.NotFound));
            if (approval.ExpiresAt <= now) return ValueTask.FromResult(new NavigatorStoreResult(NavigatorStoreOutcome.Expired));
            if (!StringComparer.Ordinal.Equals(approval.Digest, digest) || !StringComparer.Ordinal.Equals(approval.CommandId, receipt.CommandId)) return ValueTask.FromResult(new NavigatorStoreResult(NavigatorStoreOutcome.Conflict));
            _approvals.Remove(key);
            _consumed.Add(key);
            _receipts.Add(receiptKey, receipt);
            return ValueTask.FromResult(new NavigatorStoreResult(NavigatorStoreOutcome.Succeeded));
        }
    }

    public ValueTask<NavigatorReceiptRecord?> GetReceiptAsync(string tenant, string principal, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_receipts.TryGetValue((tenant, principal, idempotencyKey), out var receipt)) return ValueTask.FromResult<NavigatorReceiptRecord?>(null);
            return ValueTask.FromResult<NavigatorReceiptRecord?>(receipt.ExpiresAt <= now ? null : receipt);
        }
    }

    public ValueTask<NavigatorStoreOutcome> AppendEventAsync(string id, string tenant, string principal, long expectedSequence, NavigatorEvent value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!Owned(id, tenant, principal)) return ValueTask.FromResult(NavigatorStoreOutcome.NotFound);
            if (!NavigatorEventTypes.IsSafe(value) || value.SessionId != id) return ValueTask.FromResult(NavigatorStoreOutcome.Conflict);
            var events = _events[id];
            var next = events.Count == 0 ? 1 : events[^1].Sequence + 1;
            if (next != expectedSequence || value.Sequence != expectedSequence) return ValueTask.FromResult(NavigatorStoreOutcome.Conflict);
            events.Add(value);
            return ValueTask.FromResult(NavigatorStoreOutcome.Succeeded);
        }
    }

    public ValueTask<IReadOnlyList<NavigatorEvent>> GetEventsAsync(string id, string tenant, string principal, long afterSequence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult<IReadOnlyList<NavigatorEvent>>(Owned(id, tenant, principal) ? _events[id].Where(item => item.Sequence > afterSequence).ToArray() : Array.Empty<NavigatorEvent>());
    }

    /// <summary>
    /// Applies a process-local, tenant-scoped monotonic fence check. This adapter cannot validate
    /// lease identity or expiry; production lease validation must be wired to the durable operational store.
    /// </summary>
    public ValueTask<bool> IsFenceCurrentAsync(string tenant, string executionId, long fencingToken, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(executionId)) return ValueTask.FromResult(false);
        lock (_gate)
        {
            var key = (tenant, executionId);
            if (_observedFences.TryGetValue(key, out var current) && fencingToken < current)
                return ValueTask.FromResult(false);
            _observedFences[key] = fencingToken;
            return ValueTask.FromResult(true);
        }
    }

    private static bool ReceiptMatches(NavigatorReceiptRecord prior, NavigatorReceiptRecord candidate) =>
        StringComparer.Ordinal.Equals(prior.CommandId, candidate.CommandId)
        && StringComparer.Ordinal.Equals(prior.IdempotencyKey, candidate.IdempotencyKey)
        && StringComparer.Ordinal.Equals(prior.Digest, candidate.Digest)
        && Equals(prior.Result, candidate.Result)
        && prior.ExpiresAt == candidate.ExpiresAt
        && prior.FencingToken == candidate.FencingToken;

    private bool Owned(string id, string tenant, string principal) => _sessions.TryGetValue(id, out var session) && IsOwned(session.Session, tenant, principal);
    private static bool IsOwned(NavigatorSession session, string tenant, string principal) => session.TenantId == tenant && session.PrincipalId == principal;
}
