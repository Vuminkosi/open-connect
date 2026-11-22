using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudVoiceConsumptionStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudVoiceConsumptionStore
{
    public async Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerFingerprint, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claim = await db.ExecutionClaims.AsNoTracking().SingleOrDefaultAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.DeploymentId == deploymentId, cancellationToken);
        if (claim is null || claim.WorkerPayloadFingerprint is not { Length: 32 } ||
            !CryptographicOperations.FixedTimeEquals(claim.WorkerPayloadFingerprint, workerFingerprint))
            return "unavailable";
        var credentialId = await db.Database.SqlQuery<Guid>($"SELECT \"CredentialId\" AS \"Value\" FROM cloud.execution_claim_credentials WHERE \"ReservationId\" = {reservationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (credentialId == Guid.Empty)
            return "unavailable";
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {credentialId} AND \"TenantId\" = {claim.TenantId} AND \"ApplicationId\" = {claim.ApplicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains("voice.synthesize", StringComparer.Ordinal))
            return "unavailable";
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {claim.ApplicationId} AND \"TenantId\" = {claim.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment)
            return "unavailable";
        var capabilities = await EfCloudClaimCapabilitySnapshot.LockAsync(db, claim.TenantId, claim.ApplicationId, cancellationToken);
        var policy = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {claim.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var catalog = capabilities.Capabilities.SingleOrDefault(x => x.Id == "voice.synthesize");
        if (policy is null || catalog is null || !catalog.AllowedRegionsVerified || claim.Region is null ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(catalog.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal))
            return "unavailable";
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"Id\" = {deploymentId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (deployment is not { Enabled: true, CapabilityId: "voice.synthesize", Transport: "https-mtls" } || deployment.Region != claim.Region)
            return "unavailable";
        var matches = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM cloud.execution_claim_deployment_evidence
                WHERE "ReservationId" = {reservationId} AND "EndpointReference" = {deployment.EndpointReference}
                  AND "Transport" = {deployment.Transport} AND "ContractVersion" = {deployment.ContractVersion}
                  AND "ModelId" = {deployment.ModelId} AND "ModelRevision" = {deployment.ModelRevision}
                  AND "ModelDigest" = {deployment.ModelDigest} AND "ImageDigest" = {deployment.ImageDigest}) AS "Value"
            """).SingleAsync(cancellationToken);
        if (!matches)
            return "unavailable";
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || CloudCapabilityProjection.Application(capabilities, now).FirstOrDefault(x => x.Id == "voice.synthesize") is not { ReasonCode: null } ||
            reservation is null || reservation.TenantId != claim.TenantId || reservation.ApplicationId != claim.ApplicationId ||
            reservation.FinalizedAt is not null || reservation.ExpiresAt <= now || reservation.Amount is < 1 or > 2000 ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == claim.TenantId && x.ApplicationId == claim.ApplicationId &&
                x.CapabilityId == "voice.synthesize" && x.Unit == "character", cancellationToken))
            return "unavailable";
        if (!CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) || reservation.RequestFingerprint is not { Length: 32 } ||
            !await EfCloudVoicePrivacyAdmission.IsAllowedAsync(db, claim.TenantId, claim.ApplicationId, reservation.RequestId,
                reservation.FingerprintKeyId!, reservation.RequestFingerprint, reservation.Amount, cancellationToken))
            return "unavailable";
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_consumptions (\"ReservationId\", \"ClaimId\", \"DeploymentId\", \"ConsumedAt\") VALUES ({reservationId}, {claimId}, {deploymentId}, {now}) ON CONFLICT (\"ReservationId\") DO NOTHING", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1 ? "consumed" : "already_consumed";
    }
}
