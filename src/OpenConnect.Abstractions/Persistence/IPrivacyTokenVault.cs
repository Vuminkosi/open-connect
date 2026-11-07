namespace OpenConnect.Abstractions.Persistence;

/// <summary>Provider-independent storage contract for privacy token mappings.</summary>
public interface IPrivacyTokenVault
{
    /// <summary>Stores an encrypted privacy token binding.</summary>
    Task StoreAsync(PrivacyTokenBinding binding, CancellationToken ct = default);

    /// <summary>Gets a token binding within the specified tenant.</summary>
    Task<PrivacyTokenBinding?> GetAsync(string tokenId, TenantId tenantId, CancellationToken ct = default);

    /// <summary>Deletes bindings that have expired at or before the specified UTC time.</summary>
    Task DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}
