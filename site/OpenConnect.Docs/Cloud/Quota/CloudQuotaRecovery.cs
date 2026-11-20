namespace OpenConnect.Docs.Cloud;

public interface ICloudQuotaRecoveryStore
{
    Task<int> ReleaseUnclaimedAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken);
}

public sealed class CloudQuotaRecovery(ICloudQuotaRecoveryStore store, TimeProvider timeProvider)
{
    // The grace period limits exposure to ordinary clock skew; it does not prove worker termination.
    public Task<int> ReleaseExpiredUnclaimedAsync(CancellationToken cancellationToken) =>
        store.ReleaseUnclaimedAsync(timeProvider.GetUtcNow().AddMinutes(-5), 100, cancellationToken);
}
