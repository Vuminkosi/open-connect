using OpenConnect.Persistence.EFCore.Navigator;

namespace OpenConnect.Samples.Navigator;

/// <summary>
/// Sample-local lease adapter over the hardened <see cref="INavigatorLeaseSeam"/>.
/// Lease surface only: acquire / conflict / renew / currency / release, with the fencing
/// token as the cross-process authority. Fail-closed throughout: invalid input, live-holder
/// conflicts, stale tokens, and expired leases surface as <c>null</c>/<c>false</c>;
/// only cancellation and null-argument guards throw.
/// </summary>
/// <remarks>
/// Receipt paths are explicitly OUT OF SCOPE for this slice (receipt carrier UNDECIDED per
/// the task contract): this adapter exposes no <c>ConsumeApprovalAndSaveReceipt</c>,
/// <c>GetReceipt</c>, or <c>TrySaveReceipt</c> members and creates no receipt tables/columns.
/// </remarks>
public sealed class NavigatorLeaseAdapter(INavigatorLeaseSeam seam)
{
    private readonly INavigatorLeaseSeam _seam = seam ?? throw new ArgumentNullException(nameof(seam));

    /// <summary>
    /// Attempts to acquire the lease for an execution. Returns the lease on success,
    /// or <c>null</c> when input is invalid or a live holder exists (conflict).
    /// </summary>
    public ValueTask<NavigatorLease?> AcquireAsync(
        string executionId, string owner, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        _seam.TryAcquireAsync(executionId, owner, duration, now, cancellationToken);

    /// <summary>
    /// Renews a lease only when its fencing token matches the live stored row and the row
    /// is unexpired. Returns the renewed lease (same <c>LeaseId</c>, advanced token), or
    /// <c>null</c> on stale token, expiry, or invalid input.
    /// </summary>
    public ValueTask<NavigatorLease?> RenewAsync(
        NavigatorLease lease, TimeSpan duration, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        _seam.RenewAsync(lease, duration, now, cancellationToken);

    /// <summary>
    /// Checks whether the lease's fencing token is the current live fence.
    /// </summary>
    public ValueTask<bool> IsCurrentAsync(
        NavigatorLease lease, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        _seam.IsCurrentAsync(lease, now, cancellationToken);

    /// <summary>
    /// Releases a lease. The row is retained with a minimum expiry so the next acquire
    /// advances the fencing token. Unknown/stale tokens are an idempotent no-op.
    /// </summary>
    public ValueTask ReleaseAsync(NavigatorLease lease, CancellationToken cancellationToken = default) =>
        _seam.ReleaseAsync(lease, cancellationToken);
}
