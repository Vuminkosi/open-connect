namespace OpenConnect.Docs.Cloud;

public interface ICloudTranslationQuotaStore
{
    Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CloudTranslationQuota(ICloudTranslationQuotaStore store, TimeProvider clock)
{
    public Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (request is null || request.TenantId == Guid.Empty || request.ApplicationId == Guid.Empty || request.RequestId == Guid.Empty ||
            request.CapabilityId != "translation.translate" || request.Unit != "character" || request.Amount is < 1 or > 5000 ||
            !CloudRequestFingerprint.IsValidKeyId(request.FingerprintKeyId) || request.RequestFingerprint is not { Length: 32 } ||
            request.ExpiresAt <= now || request.ExpiresAt > now.AddMinutes(5))
            return Task.FromResult(new CloudQuotaResult("invalid_request"));
        var utcExpiry = request.ExpiresAt.ToUniversalTime();
        var storedExpiry = new DateTimeOffset(utcExpiry.Ticks - utcExpiry.Ticks % 10, TimeSpan.Zero);
        if (storedExpiry <= now) return Task.FromResult(new CloudQuotaResult("invalid_request"));
        return store.ReserveAsync(request with { ExpiresAt = storedExpiry, RequestFingerprint = request.RequestFingerprint.ToArray() }, now, cancellationToken);
    }
}
