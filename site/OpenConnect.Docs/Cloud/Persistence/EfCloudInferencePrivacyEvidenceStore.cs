using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInferencePrivacyEvidenceStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudInferencePrivacyEvidenceStore
{
    public async Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string outcome, string traceId, CancellationToken cancellationToken)
    {
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            outcome is not ("allowed" or "privacy_blocked" or "detector_unavailable") ||
            traceId.Length != 32 || traceId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid inference privacy observation.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var id = Guid.NewGuid(); var now = timeProvider.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.inference_privacy_evidence ("Id", "TenantId", "ApplicationId", "RequestId", "Outcome", "TraceId", "OccurredAt")
            VALUES ({id}, {identity.TenantId}, {identity.ApplicationId}, {requestId}, {outcome}, {traceId}, {now})
            """, cancellationToken);
    }
}
