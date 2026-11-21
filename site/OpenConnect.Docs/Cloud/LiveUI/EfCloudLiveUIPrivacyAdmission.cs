using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

internal static class EfCloudLiveUIPrivacyAdmission
{
    // Callers hold the application lock through their transaction.
    internal static Task<bool> IsAllowedAsync(CloudControlPlaneDbContext db, Guid tenantId, Guid applicationId,
        Guid requestId, string fingerprintKeyId, byte[] fingerprint, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            SELECT COALESCE((SELECT "Outcome" = 'allowed' AND "FingerprintKeyId" = {fingerprintKeyId} AND "RequestFingerprint" = {fingerprint}
                FROM cloud.liveui_privacy_evidence WHERE "TenantId" = {tenantId} AND "ApplicationId" = {applicationId}
                    AND "RequestId" = {requestId} ORDER BY "Sequence" DESC LIMIT 1), false) AS "Value"
            """).SingleAsync(cancellationToken);
}
