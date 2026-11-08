using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.Operational.Durable;

namespace OpenConnect.Navigator.DurableStore;

/// <summary>Forwards each successfully committed event to the commit hook.</summary>
/// <remarks>Hook failures propagate after the durable commit has succeeded; callers must treat that as a delivery failure, not a rolled-back commit.</remarks>
public sealed class CommittingDurableNavigatorStore(IServiceProvider services, INavigatorCommitHook hook) : IDurableNavigatorStore
{
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly INavigatorCommitHook _hook = hook ?? throw new ArgumentNullException(nameof(hook));

    private IDurableNavigatorStore Inner
    {
        get
        {
            var stores = _services.GetService(typeof(IEnumerable<IDurableNavigatorStore>)) as IEnumerable<IDurableNavigatorStore>;
            var inners = stores?.Where(store => !ReferenceEquals(store, this)).ToArray() ?? [];
            return inners.Length switch
            {
                1 => inners[0],
                0 => throw new InvalidOperationException("CommittingDurableNavigatorStore requires exactly one other IDurableNavigatorStore registration; none was found."),
                _ => throw new InvalidOperationException($"CommittingDurableNavigatorStore requires exactly one other IDurableNavigatorStore registration; found {inners.Length}."),
            };
        }
    }

    public ValueTask<NavigatorSession?> GetSessionAsync(string id, string tenant, string principal, CancellationToken cancellationToken = default) => Inner.GetSessionAsync(id, tenant, principal, cancellationToken);
    public ValueTask<NavigatorStoreOutcome> UpdateSessionSnapshotAsync(string id, string tenant, string principal, NavigatorSessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) => Inner.UpdateSessionSnapshotAsync(id, tenant, principal, snapshot, expectedRevision, cancellationToken);
    public ValueTask<string> CreateSessionAsync(string tenant, string principal, string clientInstance, string routeId, CancellationToken cancellationToken = default) => Inner.CreateSessionAsync(tenant, principal, clientInstance, routeId, cancellationToken);
    public ValueTask<NavigatorStoreOutcome> CreatePendingApprovalAsync(string id, string tenant, string principal, NavigatorApprovalRecord approval, CancellationToken cancellationToken = default) => Inner.CreatePendingApprovalAsync(id, tenant, principal, approval, cancellationToken);
    public ValueTask<NavigatorStoreResult> ConsumeApprovalAndSaveReceiptAsync(string id, string tenant, string principal, string commandId, string digest, DateTimeOffset now, NavigatorReceiptRecord receipt, CancellationToken cancellationToken = default) => Inner.ConsumeApprovalAndSaveReceiptAsync(id, tenant, principal, commandId, digest, now, receipt, cancellationToken);
    public ValueTask<NavigatorReceiptRecord?> GetReceiptAsync(string tenant, string principal, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken = default) => Inner.GetReceiptAsync(tenant, principal, idempotencyKey, now, cancellationToken);

    public async ValueTask<NavigatorStoreOutcome> AppendEventAsync(string id, string tenant, string principal, long expectedSequence, NavigatorEvent value, CancellationToken cancellationToken = default)
    {
        var result = await Inner.AppendEventAsync(id, tenant, principal, expectedSequence, value, cancellationToken).ConfigureAwait(false);
        if (result == NavigatorStoreOutcome.Succeeded)
            await _hook.AfterCommitAsync(id, [value], cancellationToken).ConfigureAwait(false);
        return result;
    }

    public ValueTask<IReadOnlyList<NavigatorEvent>> GetEventsAsync(string id, string tenant, string principal, long afterSequence, CancellationToken cancellationToken = default) => Inner.GetEventsAsync(id, tenant, principal, afterSequence, cancellationToken);
    public ValueTask<bool> IsFenceCurrentAsync(string tenant, string executionId, long fencingToken, DateTimeOffset now, CancellationToken cancellationToken = default) => Inner.IsFenceCurrentAsync(tenant, executionId, fencingToken, now, cancellationToken);
}
