namespace OpenConnect.Navigator.Operational;

/// <summary>Process-local fallback only; not durable and cannot provide cross-process recovery.</summary>
public sealed class InMemoryOperationalLeaseStore : IOperationalLeaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OperationalLease> _leases = new(StringComparer.Ordinal);
    private long _fencingToken;
    public bool IsDurable => false;

    public ValueTask<OperationalLease?> TryAcquireAsync(string executionId, TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_leases.TryGetValue(executionId, out var current) && current.ExpiresAt > now) return ValueTask.FromResult<OperationalLease?>(null);
            var lease = new OperationalLease(Guid.NewGuid().ToString("N"), executionId, Interlocked.Increment(ref _fencingToken), now + duration);
            _leases[executionId] = lease;
            return ValueTask.FromResult<OperationalLease?>(lease);
        }
    }

    public ValueTask<OperationalLease?> RenewAsync(OperationalLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!IsCurrent(lease) || lease.ExpiresAt <= DateTimeOffset.UtcNow) return ValueTask.FromResult<OperationalLease?>(null);
            var renewed = lease with { ExpiresAt = DateTimeOffset.UtcNow + duration };
            _leases[lease.ExecutionId] = renewed;
            return ValueTask.FromResult<OperationalLease?>(renewed);
        }
    }

    public ValueTask<bool> IsCurrentAsync(OperationalLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(IsCurrent(lease) && lease.ExpiresAt > DateTimeOffset.UtcNow);
    }

    public ValueTask ReleaseAsync(OperationalLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (IsCurrent(lease)) _leases.Remove(lease.ExecutionId);
        return ValueTask.CompletedTask;
    }

    private bool IsCurrent(OperationalLease lease) => _leases.TryGetValue(lease.ExecutionId, out var current)
        && current.LeaseId == lease.LeaseId && current.FencingToken == lease.FencingToken;
}
