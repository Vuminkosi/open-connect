namespace OpenConnect.Docs.Cloud;

public sealed record CloudLiveUIDualQuotaRequest(Guid TenantId, Guid ApplicationId, Guid RequestId,
    string FingerprintKeyId, byte[] RequestFingerprint, long TokenAmount, DateTimeOffset ExpiresAt);

public interface ICloudLiveUIDualQuotaStore
{
    Task<CloudQuotaResult> ReserveAsync(CloudLiveUIDualQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>Validates a combined LiveUI plan and token reservation request.</summary>
public sealed class CloudLiveUIDualQuotaService(ICloudLiveUIDualQuotaStore store, TimeProvider timeProvider)
{
    public Task<CloudQuotaResult> ReserveAsync(CloudLiveUIDualQuotaRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (request is null || request.TenantId == Guid.Empty || request.ApplicationId == Guid.Empty || request.RequestId == Guid.Empty ||
            !CloudRequestFingerprint.IsValidKeyId(request.FingerprintKeyId) || request.RequestFingerprint is not { Length: 32 } ||
            request.TokenAmount is < 1 or > 32768 || request.ExpiresAt <= now)
            return Task.FromResult(new CloudQuotaResult("invalid_request"));
        var utcExpiry = request.ExpiresAt.ToUniversalTime();
        var expiry = new DateTimeOffset(utcExpiry.Ticks - utcExpiry.Ticks % 10, TimeSpan.Zero);
        return expiry <= now ? Task.FromResult(new CloudQuotaResult("invalid_request")) :
            store.ReserveAsync(request with { ExpiresAt = expiry, RequestFingerprint = request.RequestFingerprint.ToArray() }, now, cancellationToken);
    }
}
