using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public interface ICloudAnalyticsPrivacyEvidenceStore
{
    Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
        string direction, string outcome, string traceId, IReadOnlyList<CloudPrivacyCategoryCount> categories, CancellationToken cancellationToken);
}

public sealed class EfCloudAnalyticsPrivacyEvidenceStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider)
    : ICloudAnalyticsPrivacyEvidenceStore
{
    public async Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
        string direction, string outcome, string traceId, IReadOnlyList<CloudPrivacyCategoryCount> categories, CancellationToken cancellationToken)
    {
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            !CloudRequestFingerprint.IsValidKeyId(fingerprintKeyId) || fingerprint is not { Length: 32 } ||
            direction is not ("request" or "response") || outcome is not ("allowed" or "privacy_blocked" or "detector_unavailable") ||
            traceId.Length != 32 || traceId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid analytics privacy observation.");
        var categoryCounts = CloudPrivacyCategories.Encode(categories, outcome);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize decision updates against claim and consumption admission.
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {identity.ApplicationId} AND \"TenantId\" = {identity.TenantId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null) throw new InvalidOperationException("Application unavailable.");
        var id = Guid.NewGuid(); var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.analytics_privacy_evidence ("Id", "TenantId", "ApplicationId", "RequestId", "Direction", "Outcome", "TraceId", "OccurredAt", "FingerprintKeyId", "RequestFingerprint", "CategoryCounts")
            VALUES ({id}, {identity.TenantId}, {identity.ApplicationId}, {requestId}, {direction}, {outcome}, {traceId}, {now}, {fingerprintKeyId}, {fingerprint}, {categoryCounts})
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
