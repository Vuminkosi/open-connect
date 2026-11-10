using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Navigator;
using OpenConnect.Persistence.EFCore.Stores;

namespace OpenConnect.WorkerHosts.Operational;

/// <summary>
/// Durable <see cref="IDurableOperationalStore"/> composed from public EF Core seams only:
/// execution leases ride <see cref="INavigatorLeaseSeam"/> (fencing token monotonic across
/// processes) and completed-run receipts ride <see cref="EfNavigatorReceiptStore{TContext}"/>
/// (first-writer wins). No persistence internals are duplicated; shape mapping only.
/// </summary>
internal sealed class EfOperationalDurableStore(
    INavigatorLeaseSeam leases,
    EfNavigatorReceiptStore<OpenConnectDbContext> receipts) : IDurableOperationalStore
{
    public bool IsDurable => true;

    public async ValueTask<OperationalLease?> TryAcquireLeaseAsync(string executionId, string owner, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lease = await leases.TryAcquireAsync(executionId, owner, duration, now, cancellationToken).ConfigureAwait(false);
        return lease is null ? null : new OperationalLease(lease.LeaseId, lease.ExecutionId, lease.FencingToken, lease.ExpiresAt);
    }

    public async ValueTask<OperationalLease?> RenewLeaseAsync(OperationalLease lease, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var renewed = await leases.RenewAsync(new NavigatorLease(lease.LeaseId, lease.ExecutionId, lease.FencingToken, lease.ExpiresAt), duration, now, cancellationToken).ConfigureAwait(false);
        return renewed is null ? null : new OperationalLease(renewed.LeaseId, renewed.ExecutionId, renewed.FencingToken, renewed.ExpiresAt);
    }

    public ValueTask<bool> IsLeaseCurrentAsync(OperationalLease lease, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return leases.IsCurrentAsync(new NavigatorLease(lease.LeaseId, lease.ExecutionId, lease.FencingToken, lease.ExpiresAt), now, cancellationToken);
    }

    public ValueTask ReleaseLeaseAsync(OperationalLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return leases.ReleaseAsync(new NavigatorLease(lease.LeaseId, lease.ExecutionId, lease.FencingToken, lease.ExpiresAt), cancellationToken);
    }

    public async ValueTask<OperationalReceipt?> GetReceiptAsync(string tenantScope, string idempotencyKey, CancellationToken cancellationToken)
    {
        var view = await receipts.GetOperationalReceiptAsync(tenantScope, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return view is null ? null : new OperationalReceipt(view.ExecutionId, MapStatus(view.Status), view.CompletedSteps, view.SafeSummary, view.FencingToken);
    }

    public ValueTask<bool> TrySaveReceiptAsync(string tenantScope, string idempotencyKey, OperationalReceipt receipt, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Status != OperationalRunStatus.Completed) return ValueTask.FromResult(false);
        var input = new EfOperationalReceiptInput(receipt.ExecutionId, MapStatus(receipt.Status), receipt.CompletedSteps, receipt.SafeSummary, receipt.FencingToken);
        return receipts.TrySaveOperationalReceiptAsync(tenantScope, idempotencyKey, input, completedAt, cancellationToken);
    }

    private static OperationalRunStatus MapStatus(EfOperationalReceiptStatus status) => status switch
    {
        EfOperationalReceiptStatus.Completed => OperationalRunStatus.Completed,
        EfOperationalReceiptStatus.Cancelled => OperationalRunStatus.Cancelled,
        EfOperationalReceiptStatus.Failed => OperationalRunStatus.Failed,
        EfOperationalReceiptStatus.Uncertain => OperationalRunStatus.Uncertain,
        EfOperationalReceiptStatus.LeaseLost => OperationalRunStatus.LeaseLost,
        EfOperationalReceiptStatus.Rejected => OperationalRunStatus.Rejected,
        _ => OperationalRunStatus.Rejected,
    };

    private static EfOperationalReceiptStatus MapStatus(OperationalRunStatus status) => status switch
    {
        OperationalRunStatus.Completed => EfOperationalReceiptStatus.Completed,
        OperationalRunStatus.Cancelled => EfOperationalReceiptStatus.Cancelled,
        OperationalRunStatus.Failed => EfOperationalReceiptStatus.Failed,
        OperationalRunStatus.Uncertain => EfOperationalReceiptStatus.Uncertain,
        OperationalRunStatus.LeaseLost => EfOperationalReceiptStatus.LeaseLost,
        OperationalRunStatus.Rejected => EfOperationalReceiptStatus.Rejected,
        _ => EfOperationalReceiptStatus.Rejected,
    };
}
