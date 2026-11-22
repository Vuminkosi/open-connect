using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudTranslationClaimStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudTranslationClaimStore
{
    public async Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
        byte[] fingerprint, byte[] workerFingerprint, CloudTranslationDeploymentMatchRequest deployment, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {identity.CredentialId} AND \"TenantId\" = {identity.TenantId} AND \"ApplicationId\" = {identity.ApplicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null || credential.ExpiresAt <= now ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains("translation.translate", StringComparer.Ordinal))
            return new("unavailable");
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {identity.ApplicationId} AND \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment || application.Environment != identity.Environment)
            return new("unavailable");
        var snapshot = await EfCloudClaimCapabilitySnapshot.LockAsync(db, identity.TenantId, identity.ApplicationId, cancellationToken);
        if (CloudCapabilityProjection.Application(snapshot, now).FirstOrDefault(x => x.Id == "translation.translate") is not { ReasonCode: null })
            return new("unavailable");
        var policy = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var capability = snapshot.Capabilities.SingleOrDefault(x => x.Id == "translation.translate");
        if (policy is null || capability is null || !capability.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(deployment.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(capability.AllowedRegions) ?? []).Contains(deployment.Region, StringComparer.Ordinal))
            return new("unavailable");
        var worker = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"EndpointReference\" = {deployment.EndpointReference} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (worker is not { Enabled: true, CapabilityId: "translation.translate", Transport: "https-mtls" } || worker.Id != deployment.DeploymentId || worker.Region != deployment.Region ||
            worker.ContractVersion != deployment.ContractVersion || worker.ModelId != deployment.ModelId || worker.ModelRevision != deployment.ModelRevision ||
            worker.ModelDigest != deployment.ModelDigest || worker.ImageDigest != deployment.ImageDigest)
            return new("unavailable");
        // Profile writers take the same deployment lock before changing the pair set.
        var profileRevision = await db.Database.SqlQuery<long>($"""
            SELECT p."Revision" AS "Value" FROM cloud.translation_deployment_profiles p
            WHERE p."DeploymentId" = {worker.Id} AND p."DeploymentRevision" = {worker.Revision} AND p."TokenizerHash" = {deployment.TokenizerHash}
                AND EXISTS (SELECT 1 FROM cloud.translation_deployment_pairs l WHERE l."DeploymentId" = p."DeploymentId"
                    AND l."SourceLang" = {deployment.SourceLang} AND l."TargetLang" = {deployment.TargetLang})
            """).SingleOrDefaultAsync(cancellationToken);
        if (profileRevision <= 0)
            return new("unavailable");
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} AND \"TenantId\" = {identity.TenantId} AND \"ApplicationId\" = {identity.ApplicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || CloudCapabilityProjection.Application(snapshot, now).FirstOrDefault(x => x.Id == "translation.translate") is not { ReasonCode: null } ||
            reservation is null || reservation.FinalizedAt is not null || reservation.ExpiresAt <= now || reservation.Amount is < 1 or > 5000 ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == identity.TenantId && x.ApplicationId == identity.ApplicationId &&
                x.CapabilityId == "translation.translate" && x.Unit == "character", cancellationToken))
            return new("unavailable");
        if (!string.Equals(reservation.FingerprintKeyId, fingerprintKeyId, StringComparison.Ordinal) || reservation.RequestFingerprint is not { Length: 32 } ||
            !CryptographicOperations.FixedTimeEquals(reservation.RequestFingerprint, fingerprint))
            return new("idempotency_conflict");
        if (await db.ExecutionClaims.AnyAsync(x => x.ReservationId == reservationId, cancellationToken))
            return new("already_claimed");
        if (!await EfCloudTranslationPrivacyAdmission.IsAllowedAsync(db, identity.TenantId, identity.ApplicationId,
            reservation.RequestId, fingerprintKeyId, fingerprint, reservation.Amount, cancellationToken))
            return new("unavailable");
        if (await (from claim in db.ExecutionClaims
                   join held in db.QuotaReservations on claim.ReservationId equals held.Id
                   where claim.DeploymentId == worker.Id && held.FinalizedAt == null
                   select claim.ReservationId).CountAsync(cancellationToken) >= worker.ConcurrencyLimit)
            return new("unavailable");
        var created = new CloudExecutionClaim
        {
            ReservationId = reservationId,
            TenantId = identity.TenantId,
            ApplicationId = identity.ApplicationId,
            ClaimId = Guid.NewGuid(),
            DeploymentId = worker.Id,
            Region = worker.Region,
            WorkerPayloadFingerprint = workerFingerprint,
            ClaimedAt = now
        };
        db.ExecutionClaims.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_claim_credentials (\"ReservationId\", \"CredentialId\") VALUES ({reservationId}, {identity.CredentialId})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.execution_claim_deployment_evidence ("ReservationId", "EndpointReference", "Transport", "ContractVersion", "ModelId", "ModelRevision", "ModelDigest", "ImageDigest")
            VALUES ({reservationId}, {worker.EndpointReference}, {worker.Transport}, {worker.ContractVersion}, {worker.ModelId}, {worker.ModelRevision}, {worker.ModelDigest}, {worker.ImageDigest})
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.translation_claim_evidence ("ReservationId", "DeploymentRevision", "ProfileRevision", "TokenizerHash", "SourceLang", "TargetLang")
            VALUES ({reservationId}, {worker.Revision}, {profileRevision}, {deployment.TokenizerHash}, {deployment.SourceLang}, {deployment.TargetLang})
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("claimed", created.ClaimId);
    }
}
