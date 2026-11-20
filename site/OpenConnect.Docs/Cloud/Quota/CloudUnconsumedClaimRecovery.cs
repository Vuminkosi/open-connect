namespace OpenConnect.Docs.Cloud;

public interface ICloudUnconsumedClaimRecoveryStore
{
    Task<int> ReleaseAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken);
}

public sealed class CloudUnconsumedClaimRecovery(ICloudUnconsumedClaimRecoveryStore store, TimeProvider timeProvider)
{
    public Task<int> ReleaseExpiredAsync(CancellationToken cancellationToken) =>
        store.ReleaseAsync(timeProvider.GetUtcNow().AddMinutes(-5), 100, cancellationToken);
}
