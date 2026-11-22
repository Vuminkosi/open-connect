using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

internal static class EfCloudAnalyticsPrivacyAdmission
{
    // The caller holds the application share lock through claim or consumption commit.
    internal static Task<bool> IsAllowedAsync(CloudControlPlaneDbContext db, Guid tenantId, Guid applicationId,
        Guid requestId, string fingerprintKeyId, byte[] fingerprint, CancellationToken cancellationToken) =>
        IsDirectionAllowedAsync(db, tenantId, applicationId, requestId, fingerprintKeyId, fingerprint, "request", cancellationToken);

    internal static Task<bool> IsDirectionAllowedAsync(CloudControlPlaneDbContext db, Guid tenantId, Guid applicationId,
        Guid requestId, string fingerprintKeyId, byte[] fingerprint, string direction, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            SELECT COALESCE((SELECT "Outcome" = 'allowed' AND "FingerprintKeyId" = {fingerprintKeyId}
                AND "RequestFingerprint" = {fingerprint} FROM cloud.analytics_privacy_evidence
                WHERE "TenantId" = {tenantId} AND "ApplicationId" = {applicationId} AND "RequestId" = {requestId} AND "Direction" = {direction}
                ORDER BY "Sequence" DESC LIMIT 1), false) AS "Value"
            """).SingleAsync(cancellationToken);
}
