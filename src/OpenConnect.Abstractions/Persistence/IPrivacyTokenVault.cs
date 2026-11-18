namespace OpenConnect.Abstractions.Persistence;

/// <summary>Provider-independent storage contract for privacy token mappings.</summary>
public interface IPrivacyTokenVault
{
    /// <summary>
    /// Stores an encrypted privacy token binding. A binding for the same tenant, principal or workload scope, token id and session
    /// replaces the existing one (idempotent re-store).
    /// </summary>
    Task StoreAsync(PrivacyTokenBinding binding, CancellationToken ct = default);

    /// <summary>
    /// Gets an unexpired token binding owned by the exact tenant, principal or workload scope, and session.
    /// Returns <see langword="null"/> when no such binding exists, it has expired, or it belongs to another scope.
    /// </summary>
    Task<PrivacyTokenBinding?> GetAsync(string tokenId, TenantId tenantId, string principalOrWorkloadScope, SessionId sessionId, CancellationToken ct = default);

    /// <summary>Deletes bindings that have expired at or before the specified UTC time.</summary>
    Task DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}
