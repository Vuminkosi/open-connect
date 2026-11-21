using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

// Revalidate the immutable claim against current credential, entitlement, model, region and deployment policy.
public sealed class EfCloudLiveUIAccessStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudLiveUIAccessStore
{
    public async Task<bool> IsClaimAllowedAsync(CloudCredentialIdentity identity, Guid reservationId, Guid claimId, CancellationToken cancellationToken)
    {
        if (identity is null || identity.CredentialId == Guid.Empty || identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty ||
            reservationId == Guid.Empty || claimId == Guid.Empty) return false;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claim = await db.ExecutionClaims.AsNoTracking().SingleOrDefaultAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId &&
            x.TenantId == identity.TenantId && x.ApplicationId == identity.ApplicationId, cancellationToken);
        if (claim?.DeploymentId is not Guid deploymentId || claim.Region is null) return false;
        var credentialId = await db.Database.SqlQuery<Guid>($"SELECT \"CredentialId\" AS \"Value\" FROM cloud.execution_claim_credentials WHERE \"ReservationId\" = {reservationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (credentialId != identity.CredentialId) return false;
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {credentialId} AND \"TenantId\" = {identity.TenantId} AND \"ApplicationId\" = {identity.ApplicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null || credential.ExpiresAt <= now ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains("inference.liveui", StringComparer.Ordinal)) return false;
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {identity.ApplicationId} AND \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment || application.Environment != identity.Environment) return false;
        var snapshot = await EfCloudClaimCapabilitySnapshot.LockAsync(db, identity.TenantId, identity.ApplicationId, cancellationToken);
        if (CloudCapabilityProjection.Application(snapshot, now).FirstOrDefault(x => x.Id == "inference.liveui") is not { ReasonCode: null }) return false;
        var modelId = await db.Database.SqlQuery<string>($"SELECT \"ModelId\" AS \"Value\" FROM cloud.execution_claim_inference_models WHERE \"ReservationId\" = {reservationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (!await EfCloudInferenceModelAdmission.LockAllowedAsync(db, identity.TenantId, modelId, cancellationToken)) return false;
        var region = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var capability = snapshot.Capabilities.SingleOrDefault(x => x.Id == "inference.liveui");
        if (region is null || capability is null || !capability.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(region.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(capability.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal)) return false;
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"Id\" = {deploymentId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (deployment is null || !deployment.Enabled || deployment.CapabilityId != "inference.liveui" || deployment.Region != claim.Region || deployment.ModelId != modelId) return false;
        var matches = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM cloud.execution_claim_deployment_evidence
                WHERE "ReservationId" = {reservationId} AND "EndpointReference" = {deployment.EndpointReference}
                  AND "Transport" = {deployment.Transport} AND "ContractVersion" = {deployment.ContractVersion}
                  AND "ModelId" = {deployment.ModelId} AND "ModelRevision" = {deployment.ModelRevision}
                  AND "ModelDigest" = {deployment.ModelDigest} AND "ImageDigest" = {deployment.ImageDigest}) AS "Value"
            """).SingleAsync(cancellationToken);
        if (!matches) return false;
        var reservation = await db.QuotaReservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reservationId &&
            x.TenantId == identity.TenantId && x.ApplicationId == identity.ApplicationId, cancellationToken);
        if (reservation is null || reservation.ExpiresAt <= now || reservation.Amount != 1 ||
            (reservation.FinalizedAt is not null && reservation.Actual != 1) || !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId &&
            x.TenantId == identity.TenantId && x.ApplicationId == identity.ApplicationId && x.CapabilityId == "inference.liveui" && x.Unit == "plan", cancellationToken)) return false;
        if (!CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) || reservation.RequestFingerprint is not { Length: 32 } ||
            !await EfCloudLiveUIPrivacyAdmission.IsAllowedAsync(db, identity.TenantId, identity.ApplicationId, reservation.RequestId,
                reservation.FingerprintKeyId!, reservation.RequestFingerprint, cancellationToken)) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
