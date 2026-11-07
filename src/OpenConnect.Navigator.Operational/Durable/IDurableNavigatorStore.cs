using OpenConnect.Navigator;

namespace OpenConnect.Navigator.Operational.Durable;

public enum NavigatorStoreOutcome { Succeeded, NotFound, Conflict, Expired, AlreadyConsumed, StaleFence }
public sealed record NavigatorStoreResult(NavigatorStoreOutcome Outcome, bool IsReplay = false);

public sealed record NavigatorApprovalRecord(string SessionId, string CommandId, string Digest, DateTimeOffset ExpiresAt);
public sealed record NavigatorReceiptRecord(string CommandId, string IdempotencyKey, string Digest, object? Result, DateTimeOffset ExpiresAt, long FencingToken);

/// <summary>Asynchronous, tenant/principal-scoped durable Navigator state boundary.</summary>
public interface IDurableNavigatorStore
{
    ValueTask<NavigatorSession?> GetSessionAsync(string id, string tenant, string principal, CancellationToken cancellationToken = default);
    /// <summary>Persists a validated session snapshot only when its stored revision matches the caller's expected revision.</summary>
    /// <remarks>Invalid snapshots return <see cref="NavigatorStoreOutcome.Conflict"/>. Missing legacy Navigator checkpoint data fails closed on reads.</remarks>
    ValueTask<NavigatorStoreOutcome> UpdateSessionSnapshotAsync(string id, string tenant, string principal, NavigatorSessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default);
    ValueTask<string> CreateSessionAsync(string tenant, string principal, string clientInstance, string routeId, CancellationToken cancellationToken = default);
    ValueTask<NavigatorStoreOutcome> CreatePendingApprovalAsync(string id, string tenant, string principal, NavigatorApprovalRecord approval, CancellationToken cancellationToken = default);
    ValueTask<NavigatorStoreResult> ConsumeApprovalAndSaveReceiptAsync(string id, string tenant, string principal, string commandId, string digest, DateTimeOffset now, NavigatorReceiptRecord receipt, CancellationToken cancellationToken = default);
    /// <summary>Looks up a receipt within the specified tenant and principal scope.</summary>
    /// <param name="tenant">The tenant that owns the receipt.</param>
    /// <param name="principal">The principal that owns the receipt.</param>
    /// <param name="idempotencyKey">The key identifying the receipt.</param>
    /// <param name="now">The current time used to determine whether the receipt has expired.</param>
    /// <param name="cancellationToken">A token that can cancel the lookup.</param>
    /// <returns>The receipt when it exists and has not expired; otherwise, <see langword="null"/>.</returns>
    /// <remarks>Expired receipts are treated as absent by lookup. Use the consume path when retry semantics require an explicit <see cref="NavigatorStoreOutcome.Expired"/> outcome.</remarks>
    ValueTask<NavigatorReceiptRecord?> GetReceiptAsync(string tenant, string principal, string idempotencyKey, DateTimeOffset now, CancellationToken cancellationToken = default);
    ValueTask<NavigatorStoreOutcome> AppendEventAsync(string id, string tenant, string principal, long expectedSequence, NavigatorEvent value, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<NavigatorEvent>> GetEventsAsync(string id, string tenant, string principal, long afterSequence, CancellationToken cancellationToken = default);
    /// <summary>Checks a fencing token within the supplied tenant and execution scope.</summary>
    /// <remarks>This signature is intentionally tenant-scoped; this new, uncommitted API has no committed callers to preserve.</remarks>
    ValueTask<bool> IsFenceCurrentAsync(string tenant, string executionId, long fencingToken, DateTimeOffset now, CancellationToken cancellationToken = default);
}
