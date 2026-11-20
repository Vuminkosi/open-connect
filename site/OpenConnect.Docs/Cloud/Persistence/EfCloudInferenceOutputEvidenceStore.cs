using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInferenceOutputEvidenceStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudInferenceOutputEvidenceStore
{
    public async Task<bool> AppendForCompletedAsync(CloudCredentialIdentity identity, Guid requestId, Guid reservationId, Guid claimId,
        string outcome, string traceId, CancellationToken cancellationToken)
    {
        if (identity is null || identity.CredentialId == Guid.Empty || identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            reservationId == Guid.Empty || claimId == Guid.Empty || outcome is not ("allowed" or "privacy_blocked" or "detector_unavailable") ||
            traceId is not { Length: 32 } || traceId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid inference output observation.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.inference_output_privacy_evidence
                ("Id", "TenantId", "ApplicationId", "RequestId", "ReservationId", "ClaimId", "Outcome", "TraceId", "OccurredAt")
            SELECT {id}, r."TenantId", r."ApplicationId", r."RequestId", r."Id", c."ClaimId", {outcome}, {traceId}, {now}
            FROM cloud.quota_reservations r
            JOIN cloud.quota_buckets b ON b."Id" = r."BucketId" AND b."TenantId" = r."TenantId" AND b."ApplicationId" = r."ApplicationId"
            JOIN cloud.execution_claims c ON c."ReservationId" = r."Id" AND c."TenantId" = r."TenantId" AND c."ApplicationId" = r."ApplicationId"
            JOIN cloud.execution_claim_credentials k ON k."ReservationId" = r."Id" AND k."CredentialId" = {identity.CredentialId}
            JOIN cloud.execution_completions x ON x."ReservationId" = r."Id" AND x."ClaimId" = c."ClaimId"
            WHERE r."Id" = {reservationId} AND r."RequestId" = {requestId} AND r."TenantId" = {identity.TenantId}
                AND r."ApplicationId" = {identity.ApplicationId} AND c."ClaimId" = {claimId} AND r."FinalizedAt" IS NOT NULL
                AND b."CapabilityId" = 'inference.chat' AND b."Unit" = 'token' AND x."Outcome" = 'completed'
            """, cancellationToken);
        return inserted == 1;
    }
}
