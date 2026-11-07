namespace OpenConnect.Navigator.Operational.Durable;

/// <summary>Tenant-scoped durable receipt lookup and atomic first-writer persistence.</summary>
public sealed class DurableIdempotencyStore(IDurableOperationalStore store)
{
    public ValueTask<OperationalReceipt?> GetAsync(string tenantScope, string key, CancellationToken cancellationToken = default)
    {
        Validate(tenantScope, key);
        return store.GetReceiptAsync(tenantScope, key, cancellationToken);
    }

    public ValueTask<bool> TryCompleteAsync(string tenantScope, string key, OperationalReceipt receipt,
        DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        Validate(tenantScope, key);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Status != OperationalRunStatus.Completed)
            throw new ArgumentException("Only completed results may be persisted for replay.", nameof(receipt));
        return store.TrySaveReceiptAsync(tenantScope, key, receipt, completedAt, cancellationToken);
    }

    private static void Validate(string tenantScope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
    }
}
