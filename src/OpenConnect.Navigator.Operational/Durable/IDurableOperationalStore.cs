namespace OpenConnect.Navigator.Operational.Durable;

/// <summary>
/// Atomic persistence boundary for operational leases and receipts. Implementations must
/// use a transaction/compare-and-swap so fencing tokens remain monotonic across processes.
/// </summary>
public interface IDurableOperationalStore
{
    bool IsDurable { get; }
    ValueTask<OperationalLease?> TryAcquireLeaseAsync(string executionId, string owner, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken);
    ValueTask<OperationalLease?> RenewLeaseAsync(OperationalLease lease, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken);
    ValueTask<bool> IsLeaseCurrentAsync(OperationalLease lease, DateTimeOffset now, CancellationToken cancellationToken);
    ValueTask ReleaseLeaseAsync(OperationalLease lease, CancellationToken cancellationToken);
    ValueTask<OperationalReceipt?> GetReceiptAsync(string tenantScope, string idempotencyKey, CancellationToken cancellationToken);
    ValueTask<bool> TrySaveReceiptAsync(string tenantScope, string idempotencyKey, OperationalReceipt receipt, DateTimeOffset completedAt, CancellationToken cancellationToken);
}
