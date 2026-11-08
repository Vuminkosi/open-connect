namespace OpenConnect.Navigator.Operational.Durable;

/// <summary>Lease adapter over an atomic durable store. Durability is inherited from that store.</summary>
public sealed class DurableOperationalLeaseStore(IDurableOperationalStore store, string owner) : IOperationalLeaseStore
{
    public bool IsDurable => store.IsDurable;

    public ValueTask<OperationalLease?> TryAcquireAsync(string executionId, TimeSpan duration, CancellationToken cancellationToken)
    {
        Validate(executionId, duration);
        return store.TryAcquireLeaseAsync(executionId, owner, duration, DateTimeOffset.UtcNow, cancellationToken);
    }

    public ValueTask<OperationalLease?> RenewAsync(OperationalLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        Validate(lease.ExecutionId, duration);
        return store.RenewLeaseAsync(lease, duration, DateTimeOffset.UtcNow, cancellationToken);
    }

    public ValueTask<bool> IsCurrentAsync(OperationalLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return store.IsLeaseCurrentAsync(lease, DateTimeOffset.UtcNow, cancellationToken);
    }

    public ValueTask ReleaseAsync(OperationalLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return store.ReleaseLeaseAsync(lease, cancellationToken);
    }

    private static void Validate(string executionId, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
    }
}
