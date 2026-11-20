using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInferenceConsumptionStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudInferenceConsumptionStore
{
    public async Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Claims are immutable. Legacy claims without a credential binding fail closed.
        var claim = await db.ExecutionClaims.AsNoTracking().SingleOrDefaultAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.DeploymentId == deploymentId, cancellationToken);
        if (claim is null || claim.WorkerPayloadFingerprint is not { Length: 32 } ||
            !CryptographicOperations.FixedTimeEquals(claim.WorkerPayloadFingerprint, workerPayloadFingerprint)) return "unavailable";
        var credentialId = await db.Database.SqlQuery<Guid>($"SELECT \"CredentialId\" AS \"Value\" FROM cloud.execution_claim_credentials WHERE \"ReservationId\" = {reservationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (credentialId == Guid.Empty) return "unavailable";
        // Use the same lock order as claim admission to serialize revocation and policy changes.
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {credentialId} AND \"TenantId\" = {claim.TenantId} AND \"ApplicationId\" = {claim.ApplicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains("inference.chat", StringComparer.Ordinal)) return "unavailable";
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {claim.ApplicationId} AND \"TenantId\" = {claim.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment) return "unavailable";
        var capabilities = await EfCloudClaimCapabilitySnapshot.LockAsync(db, claim.TenantId, claim.ApplicationId, cancellationToken);
        var modelId = await db.Database.SqlQuery<string>($"SELECT \"ModelId\" AS \"Value\" FROM cloud.execution_claim_inference_models WHERE \"ReservationId\" = {reservationId}").SingleOrDefaultAsync(cancellationToken);
        if (!await EfCloudInferenceModelAdmission.LockAllowedAsync(db, claim.TenantId, modelId, cancellationToken)) return "unavailable";
        var policy = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {claim.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var catalog = capabilities.Capabilities.SingleOrDefault(x => x.Id == "inference.chat");
        if (policy is null || catalog is null || !catalog.AllowedRegionsVerified || claim.Region is null ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(catalog.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal)) return "unavailable";
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"Id\" = {deploymentId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (deployment is null || !deployment.Enabled || deployment.CapabilityId != "inference.chat" || deployment.Region != claim.Region || deployment.ModelId != modelId) return "unavailable";
        // Compare the immutable claimed deployment identity while the deployment row is locked.
        var matchingEvidence = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM cloud.execution_claim_deployment_evidence
                WHERE "ReservationId" = {reservationId} AND "EndpointReference" = {deployment.EndpointReference}
                  AND "Transport" = {deployment.Transport} AND "ContractVersion" = {deployment.ContractVersion}
                  AND "ModelId" = {deployment.ModelId} AND "ModelRevision" = {deployment.ModelRevision}
                  AND "ModelDigest" = {deployment.ModelDigest} AND "ImageDigest" = {deployment.ImageDigest}) AS "Value"
            """).SingleAsync(cancellationToken);
        if (!matchingEvidence) return "unavailable";
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || CloudCapabilityProjection.Application(capabilities, now).FirstOrDefault(x => x.Id == "inference.chat")?.ReasonCode is not null ||
            reservation is null || reservation.TenantId != claim.TenantId || reservation.ApplicationId != claim.ApplicationId || reservation.FinalizedAt is not null || reservation.ExpiresAt <= now ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == reservation.TenantId && x.ApplicationId == reservation.ApplicationId && x.CapabilityId == "inference.chat" && x.Unit == "token", cancellationToken))
            return "unavailable";
        if (!CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) || reservation.RequestFingerprint is not { Length: 32 } ||
            !await EfCloudInferencePrivacyAdmission.IsAllowedAsync(db, claim.TenantId, claim.ApplicationId, reservation.RequestId,
                reservation.FingerprintKeyId!, reservation.RequestFingerprint, cancellationToken)) return "unavailable";
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_consumptions (\"ReservationId\", \"ClaimId\", \"DeploymentId\", \"ConsumedAt\") VALUES ({reservationId}, {claimId}, {deploymentId}, {now}) ON CONFLICT (\"ReservationId\") DO NOTHING", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1 ? "consumed" : "already_consumed";
    }
}
