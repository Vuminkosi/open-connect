using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudLiveUIClaimStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudLiveUIClaimStore
{
    public async Task<CloudExecutionClaimResult> ClaimAsync(CloudCredentialIdentity identity, Guid reservationId, string fingerprintKeyId,
        byte[] fingerprint, byte[] workerFingerprint, string modelId, CloudInferenceDeploymentMatch deployment, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {identity.CredentialId} AND \"TenantId\" = {identity.TenantId} AND \"ApplicationId\" = {identity.ApplicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null || credential.ExpiresAt <= now ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains("inference.liveui", StringComparer.Ordinal))
            return new("unavailable");
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {identity.ApplicationId} AND \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment || application.Environment != identity.Environment)
            return new("unavailable");
        var snapshot = await EfCloudClaimCapabilitySnapshot.LockAsync(db, identity.TenantId, identity.ApplicationId, cancellationToken);
        if (CloudCapabilityProjection.Application(snapshot, now).FirstOrDefault(x => x.Id == "inference.liveui") is not { ReasonCode: null } ||
            !await EfCloudInferenceModelAdmission.LockAllowedAsync(db, identity.TenantId, modelId, cancellationToken))
            return new("unavailable");
        var policy = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {identity.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var capability = snapshot.Capabilities.SingleOrDefault(x => x.Id == "inference.liveui");
        if (policy is null || capability is null || !capability.AllowedRegionsVerified ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(deployment.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(capability.AllowedRegions) ?? []).Contains(deployment.Region, StringComparer.Ordinal))
            return new("unavailable");
        var worker = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"EndpointReference\" = {deployment.EndpointReference} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (worker is not { Enabled: true, CapabilityId: "inference.liveui", Transport: "https-mtls" } || worker.Region != deployment.Region ||
            worker.ContractVersion != deployment.ContractVersion || worker.ModelId != modelId || worker.ModelRevision != deployment.ModelRevision ||
            worker.ModelDigest != deployment.ModelDigest)
            return new("unavailable");
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} AND \"TenantId\" = {identity.TenantId} AND \"ApplicationId\" = {identity.ApplicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || CloudCapabilityProjection.Application(snapshot, now).FirstOrDefault(x => x.Id == "inference.liveui") is not { ReasonCode: null } ||
            reservation is null || reservation.FinalizedAt is not null || reservation.ExpiresAt <= now || reservation.Amount != 1 ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == identity.TenantId && x.ApplicationId == identity.ApplicationId &&
                x.CapabilityId == "inference.liveui" && x.Unit == "plan", cancellationToken))
            return new("unavailable");
        if (!string.Equals(reservation.FingerprintKeyId, fingerprintKeyId, StringComparison.Ordinal) || reservation.RequestFingerprint is not { Length: 32 } ||
            !CryptographicOperations.FixedTimeEquals(reservation.RequestFingerprint, fingerprint))
            return new("idempotency_conflict");
        if (await db.ExecutionClaims.AnyAsync(x => x.ReservationId == reservationId, cancellationToken))
            return new("already_claimed");
        var privacyAllowed = await EfCloudLiveUIPrivacyAdmission.IsAllowedAsync(db, identity.TenantId, identity.ApplicationId,
            reservation.RequestId, fingerprintKeyId, fingerprint, cancellationToken);
        if (!privacyAllowed)
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
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_claim_inference_models (\"ReservationId\", \"ModelId\") VALUES ({reservationId}, {modelId})", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.execution_claim_deployment_evidence ("ReservationId", "EndpointReference", "Transport", "ContractVersion", "ModelId", "ModelRevision", "ModelDigest", "ImageDigest")
            VALUES ({reservationId}, {worker.EndpointReference}, {worker.Transport}, {worker.ContractVersion}, {worker.ModelId}, {worker.ModelRevision}, {worker.ModelDigest}, {worker.ImageDigest})
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("claimed", created.ClaimId);
    }
}
