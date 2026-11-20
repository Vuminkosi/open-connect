namespace OpenConnect.Docs.Cloud;

public sealed record CloudQuotaRequest(Guid TenantId, Guid ApplicationId, string CapabilityId, string Unit,
    Guid RequestId, string FingerprintKeyId, byte[] RequestFingerprint, long Amount, DateTimeOffset ExpiresAt);
public sealed record CloudQuotaResult(string Status, Guid? ReservationId = null);
public sealed record CloudFingerprintKeyLookup(string Status, string? KeyId = null);

public interface ICloudQuotaStore
{
    Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, DateTimeOffset now, CancellationToken cancellationToken);
    Task<CloudFingerprintKeyLookup> FindFingerprintKeyAsync(Guid tenantId, Guid applicationId, Guid requestId, CancellationToken cancellationToken);
    Task<CloudQuotaResult> FinalizeAsync(Guid tenantId, Guid applicationId, Guid reservationId, long actual, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CloudQuotaService(ICloudQuotaStore store, TimeProvider timeProvider)
{
    public Task<CloudQuotaResult> ReserveAsync(CloudQuotaRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (request.TenantId == Guid.Empty || request.ApplicationId == Guid.Empty || request.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.CapabilityId) || request.CapabilityId.Length > 80 ||
            string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Length > 80 || !CloudRequestFingerprint.IsValidKeyId(request.FingerprintKeyId) || request.RequestFingerprint is not { Length: 32 } ||
            request.Amount <= 0 || request.ExpiresAt <= now)
            return Task.FromResult(new CloudQuotaResult("invalid_request"));
        var utcExpiry = request.ExpiresAt.ToUniversalTime();
        var storedExpiry = new DateTimeOffset(utcExpiry.Ticks - utcExpiry.Ticks % 10, TimeSpan.Zero);
        if (storedExpiry <= now) return Task.FromResult(new CloudQuotaResult("invalid_request"));
        return store.ReserveAsync(request with { ExpiresAt = storedExpiry, RequestFingerprint = request.RequestFingerprint.ToArray() }, now, cancellationToken);
    }

    public Task<CloudFingerprintKeyLookup> FindFingerprintKeyAsync(Guid tenantId, Guid applicationId, Guid requestId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty || applicationId == Guid.Empty || requestId == Guid.Empty
            ? Task.FromResult(new CloudFingerprintKeyLookup("invalid_request"))
            : store.FindFingerprintKeyAsync(tenantId, applicationId, requestId, cancellationToken);

    public Task<CloudQuotaResult> FinalizeAsync(Guid tenantId, Guid applicationId, Guid reservationId, long actual, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty || applicationId == Guid.Empty || reservationId == Guid.Empty || actual < 0
            ? Task.FromResult(new CloudQuotaResult("invalid_request"))
            : store.FinalizeAsync(tenantId, applicationId, reservationId, actual, timeProvider.GetUtcNow(), cancellationToken);
}
