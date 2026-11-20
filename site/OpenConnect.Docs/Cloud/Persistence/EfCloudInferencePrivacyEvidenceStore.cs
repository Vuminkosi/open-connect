using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInferencePrivacyEvidenceStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudInferencePrivacyEvidenceStore
{
    public async Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint, string outcome, string traceId, CancellationToken cancellationToken)
    {
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            !CloudRequestFingerprint.IsValidKeyId(fingerprintKeyId) || fingerprint is not { Length: 32 } ||
            outcome is not ("allowed" or "privacy_blocked" or "detector_unavailable") ||
            traceId.Length != 32 || traceId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid inference privacy observation.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Claim admission holds a share lock on this same application through commit.
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {identity.ApplicationId} AND \"TenantId\" = {identity.TenantId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null) throw new InvalidOperationException("Application unavailable.");
        var id = Guid.NewGuid(); var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.inference_privacy_evidence ("Id", "TenantId", "ApplicationId", "RequestId", "Outcome", "TraceId", "OccurredAt", "FingerprintKeyId", "RequestFingerprint")
            VALUES ({id}, {identity.TenantId}, {identity.ApplicationId}, {requestId}, {outcome}, {traceId}, {now}, {fingerprintKeyId}, {fingerprint})
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
