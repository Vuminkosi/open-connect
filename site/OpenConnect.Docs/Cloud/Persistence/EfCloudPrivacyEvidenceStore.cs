using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudPrivacyEvidenceStore(IDbContextFactory<CloudControlPlaneDbContext> contextFactory, TimeProvider timeProvider) : ICloudPrivacyEvidenceStore
{
    public async Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string outcome, string traceId, CancellationToken cancellationToken)
    {
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            outcome is not ("allowed" or "privacy_blocked" or "detector_unavailable") ||
            traceId.Length != 32 || traceId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid Cloud privacy observation.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.PrivacyEvidence.Add(new CloudPrivacyEvidence { Id = Guid.NewGuid(), TenantId = identity.TenantId,
            ApplicationId = identity.ApplicationId, RequestId = requestId, Outcome = outcome, TraceId = traceId, OccurredAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
    }
}
