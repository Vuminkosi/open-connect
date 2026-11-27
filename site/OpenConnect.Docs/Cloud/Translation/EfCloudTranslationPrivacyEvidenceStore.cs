using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public interface ICloudTranslationPrivacyEvidenceStore
{
    Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
        int characters, string outcome, string traceId, CancellationToken cancellationToken);
}

public sealed class EfCloudTranslationPrivacyEvidenceStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider)
    : ICloudTranslationPrivacyEvidenceStore
{
    public async Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
        int characters, string outcome, string traceId, CancellationToken cancellationToken)
    {
        if (identity is null || identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            !CloudRequestFingerprint.IsValidKeyId(fingerprintKeyId) || fingerprint is not { Length: 32 } || characters is < 1 or > 5000 ||
            outcome is not ("allowed" or "privacy_blocked" or "detector_unavailable") || traceId is not { Length: 32 } ||
            traceId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid translation privacy observation.");
        var fingerprintSnapshot = fingerprint.ToArray();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {identity.ApplicationId} AND \"TenantId\" = {identity.TenantId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null)
            throw new InvalidOperationException("Application unavailable.");
        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.translation_privacy_evidence ("Id", "TenantId", "ApplicationId", "RequestId", "Outcome", "TraceId", "OccurredAt", "FingerprintKeyId", "RequestFingerprint", "Characters")
            VALUES ({id}, {identity.TenantId}, {identity.ApplicationId}, {requestId}, {outcome}, {traceId}, {now}, {fingerprintKeyId}, {fingerprintSnapshot}, {characters})
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

internal static class EfCloudTranslationPrivacyAdmission
{
    // Callers must hold the tenant-scoped application lock throughout admission.
    internal static Task<bool> IsAllowedAsync(CloudControlPlaneDbContext db, Guid tenantId, Guid applicationId,
        Guid requestId, string fingerprintKeyId, byte[] fingerprint, long characters, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            SELECT COALESCE((SELECT "Outcome" = 'allowed' AND "FingerprintKeyId" = {fingerprintKeyId} AND "RequestFingerprint" = {fingerprint}
                AND "Characters" = {characters} FROM cloud.translation_privacy_evidence
                WHERE "TenantId" = {tenantId} AND "ApplicationId" = {applicationId} AND "RequestId" = {requestId}
                ORDER BY "Sequence" DESC LIMIT 1), false) AS "Value"
            """).SingleAsync(cancellationToken);
}
