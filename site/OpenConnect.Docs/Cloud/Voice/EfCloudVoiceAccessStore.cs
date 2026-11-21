using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

// This check withdraws access to an existing claim; it never creates a new claim.
public sealed class EfCloudVoiceAccessStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudVoiceAccessStore
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
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains("voice.synthesize", StringComparer.Ordinal)) return false;
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {identity.ApplicationId} AND \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment || application.Environment != identity.Environment) return false;
        var snapshot = await EfCloudClaimCapabilitySnapshot.LockAsync(db, identity.TenantId, identity.ApplicationId, cancellationToken);
        var region = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var capability = snapshot.Capabilities.SingleOrDefault(x => x.Id == "voice.synthesize");
        if (region is null || capability is null || !capability.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(region.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(capability.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal)) return false;
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"Id\" = {deploymentId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (deployment is not { Enabled: true, CapabilityId: "voice.synthesize", Transport: "https-mtls" } || deployment.Region != claim.Region) return false;
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
        var now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || CloudCapabilityProjection.Application(snapshot, now).FirstOrDefault(x => x.Id == "voice.synthesize") is not { ReasonCode: null } ||
            reservation is null || reservation.ExpiresAt <= now || reservation.Amount is < 1 or > 2000 ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == identity.TenantId && x.ApplicationId == identity.ApplicationId &&
                x.CapabilityId == "voice.synthesize" && x.Unit == "character", cancellationToken)) return false;
        if (reservation.FinalizedAt is not null && (reservation.Actual != reservation.Amount ||
            !await (from consumption in db.ExecutionConsumptions join completion in db.ExecutionCompletions
                on new { consumption.ReservationId, consumption.ClaimId } equals new { completion.ReservationId, completion.ClaimId }
                where consumption.ReservationId == reservationId && consumption.ClaimId == claimId && consumption.DeploymentId == deploymentId &&
                    completion.Outcome == "completed" select consumption.ReservationId).AnyAsync(cancellationToken))) return false;
        if (!CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) || reservation.RequestFingerprint is not { Length: 32 } ||
            !await EfCloudVoicePrivacyAdmission.IsAllowedAsync(db, identity.TenantId, identity.ApplicationId, reservation.RequestId,
                reservation.FingerprintKeyId!, reservation.RequestFingerprint, reservation.Amount, cancellationToken)) return false;
        now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || reservation.ExpiresAt <= now ||
            CloudCapabilityProjection.Application(snapshot, now).FirstOrDefault(x => x.Id == "voice.synthesize") is not { ReasonCode: null }) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
