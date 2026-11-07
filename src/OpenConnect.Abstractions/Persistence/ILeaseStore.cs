namespace OpenConnect.Abstractions.Persistence;

/// <summary>Provider-independent storage contract for distributed leases.</summary>
public interface ILeaseStore
{
    /// <summary>Gets a lease within the specified tenant.</summary>
    Task<LeaseRecord?> GetAsync(string resourceKey, TenantId tenantId, CancellationToken ct = default);

    /// <summary>Attempts to acquire a lease.</summary>
    Task<bool> TryAcquireAsync(LeaseRecord lease, CancellationToken ct = default);

    /// <summary>Renews a lease if its owner and fencing version still match.</summary>
    Task<bool> RenewAsync(string resourceKey, TenantId tenantId, string owner, long fencingVersion, DateTimeOffset expiresAt, CancellationToken ct = default);

    /// <summary>Releases a lease owned by the specified owner.</summary>
    Task ReleaseAsync(string resourceKey, TenantId tenantId, string owner, CancellationToken ct = default);
}
