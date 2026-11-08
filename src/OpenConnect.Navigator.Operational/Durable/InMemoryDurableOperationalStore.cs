namespace OpenConnect.Navigator.Operational.Durable;

/// <summary>
/// Process-local test/development adapter only. Despite implementing the persistence boundary,
/// this store is explicitly non-durable and provides no recovery across process restarts.
/// </summary>
public sealed class InMemoryDurableOperationalStore : IDurableOperationalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (OperationalLease Lease, string Owner)> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Tenant, string Key), (OperationalReceipt Receipt, DateTimeOffset CompletedAt)> _receipts = new();
    private readonly Dictionary<string, long> _fences = new(StringComparer.Ordinal);

    public bool IsDurable => false;

    public ValueTask<OperationalLease?> TryAcquireLeaseAsync(string executionId, string owner, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            if (_leases.TryGetValue(executionId, out var current) && current.Lease.ExpiresAt > now) return ValueTask.FromResult<OperationalLease?>(null);
            var token = _fences.TryGetValue(executionId, out var prior) ? checked(prior + 1) : 1;
            _fences[executionId] = token;
            var lease = new OperationalLease(Guid.NewGuid().ToString("N"), executionId, token, now + duration);
            _leases[executionId] = (lease, owner);
            return ValueTask.FromResult<OperationalLease?>(lease);
        }
    }

    public ValueTask<OperationalLease?> RenewLeaseAsync(OperationalLease lease, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsCurrent(lease) || lease.ExpiresAt <= now) return ValueTask.FromResult<OperationalLease?>(null);
            var renewed = lease with { ExpiresAt = now + duration };
            _leases[lease.ExecutionId] = (renewed, _leases[lease.ExecutionId].Owner);
            return ValueTask.FromResult<OperationalLease?>(renewed);
        }
    }

    public ValueTask<bool> IsLeaseCurrentAsync(OperationalLease lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(IsCurrent(lease) && lease.ExpiresAt > now);
    }

    public ValueTask ReleaseLeaseAsync(OperationalLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (IsCurrent(lease)) _leases.Remove(lease.ExecutionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<OperationalReceipt?> GetReceiptAsync(string tenantScope, string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_receipts.TryGetValue((tenantScope, idempotencyKey), out var value) ? value.Receipt : null);
    }

    public ValueTask<bool> TrySaveReceiptAsync(string tenantScope, string idempotencyKey, OperationalReceipt receipt, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (tenantScope, idempotencyKey);
            if (_receipts.ContainsKey(key)) return ValueTask.FromResult(false);
            _receipts.Add(key, (receipt, completedAt));
            return ValueTask.FromResult(true);
        }
    }

    private bool IsCurrent(OperationalLease lease) => _leases.TryGetValue(lease.ExecutionId, out var current)
        && current.Lease.LeaseId == lease.LeaseId && current.Lease.FencingToken == lease.FencingToken;
}
