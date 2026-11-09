using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Stores;

namespace OpenConnect.Navigator.Operational.Durable;

// Thin adapter routing Navigator receipt calls onto the EF Core receipt store
// (S0-4 integration note; backend slice owns this file).
//
// Prerequisites owned outside this file (NOT applied here):
//   - A ProjectReference from OpenConnect.Navigator.Operational to
//     OpenConnect.Persistence.EFCore, plus the EF Core shared-framework
//     reference required by the DbContext constraint below.
//   - InternalsVisibleTo("OpenConnect.Navigator.Operational") in
//     OpenConnect.Persistence.EFCore (see its AssemblyInfo.cs, currently only
//     OpenConnect.Persistence.EFCore.Tests), because EfNavigatorReceiptStore
//     and its contract types are intentionally store-local (internal).
// Until those land, this file is compile-blocked by design; no behavior is
// stubbed or invented to work around it.
//
// Mapping contract (1:1 with the legacy Postgres paths):
//   NavigatorReceiptRecord -> EfNavigatorReceiptInput: CommandId,
//     IdempotencyKey, Digest, ExpiresAt, and FencingToken copy verbatim;
//     Result (object?) crosses via JsonSerializer.SerializeToElement, so the
//     persisted JSON payload is identical to the legacy store's.
//   EfNavigatorReceiptView -> NavigatorReceiptRecord: all six fields copy
//     verbatim; the JsonElement Result boxes 1:1 into object? with no
//     re-parse, and the store's own expiry gate is authoritative (no second
//     gate here, legacy parity).
//   Outcomes map 1:1 by name (Succeeded, NotFound, Conflict, Expired,
//     AlreadyConsumed); IsReplay is preserved. NavigatorStoreOutcome.StaleFence
//     is never produced by this path.
//   Tenant/principal/session arguments pass through untouched; canonical Guid
//     "D" scoping stays inside the store (EfNavigatorLeaseSeam /
//     NavigatorReceiptMapping convention).
//   Replay/conflict identity follows the legacy triple (CommandId, Digest,
//     IdempotencyKey) — NOT full-record equality (Result, ExpiresAt, and
//     FencingToken are persisted opaquely and never decide a conflict).
//
// The EF store owns approvals + receipt tasks only; session snapshots, events,
// and leases are delegated to an inner IDurableNavigatorStore (decorator shape
// follows CommittingDurableNavigatorStore precedent), never re-implemented.
public sealed class EfNavigatorReceiptAdapter<TContext>(
    EfNavigatorReceiptStore<TContext> receipts,
    IDurableNavigatorStore inner) : IDurableNavigatorStore
    where TContext : DbContext
{
    public ValueTask<NavigatorSession?> GetSessionAsync(string id, string tenant, string principal, CancellationToken cancellationToken = default) =>
        inner.GetSessionAsync(id, tenant, principal, cancellationToken);

    public ValueTask<NavigatorStoreOutcome> UpdateSessionSnapshotAsync(string id, string tenant, string principal, NavigatorSessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) =>
        inner.UpdateSessionSnapshotAsync(id, tenant, principal, snapshot, expectedRevision, cancellationToken);

    public ValueTask<string> CreateSessionAsync(string tenant, string principal, string clientInstance, string routeId, CancellationToken cancellationToken = default) =>
        inner.CreateSessionAsync(tenant, principal, clientInstance, routeId, cancellationToken);

    public ValueTask<NavigatorStoreOutcome> CreatePendingApprovalAsync(string id, string tenant, string principal, NavigatorApprovalRecord approval, CancellationToken cancellationToken = default) =>
        inner.CreatePendingApprovalAsync(id, tenant, principal, approval, cancellationToken);

    public async ValueTask<NavigatorStoreResult> ConsumeApprovalAndSaveReceiptAsync(string id, string tenant, string principal, string commandId, string digest, DateTimeOffset now, NavigatorReceiptRecord receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var input = new EfNavigatorReceiptInput(
            receipt.CommandId,
            receipt.IdempotencyKey,
            receipt.Digest,
            JsonSerializer.SerializeToElement(receipt.Result),
            receipt.ExpiresAt,
            receipt.FencingToken);
        var result = await receipts.ConsumeApprovalAndSaveReceiptAsync(id, tenant, principal, commandId, digest, now, input, cancellationToken).ConfigureAwait(false);
        return new NavigatorStoreResult(MapOutcome(result.Outcome), result.IsReplay);
    }

    public async ValueTask<NavigatorReceiptRecord?> GetReceiptAsync(string tenant, string principal, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var view = await receipts.GetReceiptAsync(tenant, principal, idempotencyKey, now, cancellationToken).ConfigureAwait(false);
        return view is null
            ? null
            : new NavigatorReceiptRecord(view.CommandId, view.IdempotencyKey, view.Digest, view.Result, view.ExpiresAt, view.FencingToken);
    }

    public ValueTask<NavigatorStoreOutcome> AppendEventAsync(string id, string tenant, string principal, long expectedSequence, NavigatorEvent value, CancellationToken cancellationToken = default) =>
        inner.AppendEventAsync(id, tenant, principal, expectedSequence, value, cancellationToken);

    public ValueTask<IReadOnlyList<NavigatorEvent>> GetEventsAsync(string id, string tenant, string principal, long afterSequence, CancellationToken cancellationToken = default) =>
        inner.GetEventsAsync(id, tenant, principal, afterSequence, cancellationToken);

    public ValueTask<bool> IsFenceCurrentAsync(string tenant, string executionId, long fencingToken, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        inner.IsFenceCurrentAsync(tenant, executionId, fencingToken, now, cancellationToken);

    private static NavigatorStoreOutcome MapOutcome(EfNavigatorReceiptOutcome outcome) => outcome switch
    {
        EfNavigatorReceiptOutcome.Succeeded => NavigatorStoreOutcome.Succeeded,
        EfNavigatorReceiptOutcome.NotFound => NavigatorStoreOutcome.NotFound,
        EfNavigatorReceiptOutcome.Conflict => NavigatorStoreOutcome.Conflict,
        EfNavigatorReceiptOutcome.Expired => NavigatorStoreOutcome.Expired,
        EfNavigatorReceiptOutcome.AlreadyConsumed => NavigatorStoreOutcome.AlreadyConsumed,
        _ => NavigatorStoreOutcome.Conflict,
    };
}
