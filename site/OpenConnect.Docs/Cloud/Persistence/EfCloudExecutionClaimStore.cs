using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudExecutionClaimStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudExecutionClaimStore
{
    public async Task<CloudExecutionClaimResult> ClaimAsync(Guid credentialId, Guid tenantId, Guid applicationId, Guid reservationId, string capabilityId, string fingerprintKeyId, byte[] fingerprint, CloudClassificationDeploymentMatchRequest? deployment, CancellationToken cancellationToken, byte[]? workerPayloadFingerprint = null, string? inferenceModelId = null, CloudInferenceDeploymentMatch? inferenceDeployment = null)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Revocation and rotation update this row; keep a share lock through claim commit.
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {credentialId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var credentialNow = timeProvider.GetUtcNow();
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null || credential.ExpiresAt <= credentialNow ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains(capabilityId, StringComparer.Ordinal))
            return new("unavailable");
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {applicationId} AND \"TenantId\" = {tenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment) return new("unavailable");
        var capabilities = await EfCloudClaimCapabilitySnapshot.LockAsync(db, tenantId, applicationId, cancellationToken);
        CloudWorkerDeployment? selectedDeployment = null;
        if (capabilityId == "classification.classify")
        {
            if (!CloudClassificationDeploymentService.IsValid(deployment)) return new("unavailable");
            selectedDeployment = await EfCloudClaimDeployment.LockEligibleAsync(db, tenantId, capabilities, deployment!, cancellationToken);
            if (selectedDeployment is null) return new("unavailable");
        }
        if (capabilityId == "inference.chat")
        {
            if (workerPayloadFingerprint is not { Length: 32 } || !await EfCloudInferenceModelAdmission.LockAllowedAsync(db, tenantId, inferenceModelId, cancellationToken) || inferenceDeployment?.IsValid() != true)
                return new("unavailable");
            selectedDeployment = await EfCloudInferenceDeploymentAdmission.LockEligibleAsync(db, tenantId, capabilities, inferenceModelId!, inferenceDeployment, cancellationToken);
            if (selectedDeployment is null) return new("unavailable");
        }
        // Lock the reservation against both finalization and competing claims until the claim commits.
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || CloudCapabilityProjection.Application(capabilities, now).FirstOrDefault(x => x.Id == capabilityId)?.ReasonCode is not null ||
            !capabilities.Capabilities.Any(x => x.Id == capabilityId) || reservation is null || reservation.FinalizedAt is not null || reservation.ExpiresAt <= now ||
            reservation.RequestFingerprint is not { Length: 32 } || !CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.CapabilityId == capabilityId && (capabilityId != "inference.chat" || x.Unit == "token"), cancellationToken))
            return new("unavailable");
        if (!string.Equals(reservation.FingerprintKeyId, fingerprintKeyId, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(reservation.RequestFingerprint, fingerprint)) return new("idempotency_conflict");
        if (await db.ExecutionClaims.AnyAsync(x => x.ReservationId == reservationId, cancellationToken)) return new("already_claimed");
        if (selectedDeployment is not null && await (from existing in db.ExecutionClaims
                join held in db.QuotaReservations on existing.ReservationId equals held.Id
                where existing.DeploymentId == selectedDeployment.Id && held.FinalizedAt == null
                select existing.ReservationId).CountAsync(cancellationToken) >= selectedDeployment.ConcurrencyLimit)
            return new("unavailable");
        var claim = new CloudExecutionClaim { ReservationId = reservationId, TenantId = tenantId, ApplicationId = applicationId, ClaimId = Guid.NewGuid(),
            DeploymentId = selectedDeployment?.Id, Region = selectedDeployment?.Region, WorkerPayloadFingerprint = workerPayloadFingerprint, ClaimedAt = now };
        db.ExecutionClaims.Add(claim);
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_claim_credentials (\"ReservationId\", \"CredentialId\") VALUES ({reservationId}, {credentialId})", cancellationToken);
        if (capabilityId == "inference.chat")
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_claim_inference_models (\"ReservationId\", \"ModelId\") VALUES ({reservationId}, {inferenceModelId})", cancellationToken);
        if (selectedDeployment is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.execution_claim_deployment_evidence ("ReservationId", "EndpointReference", "Transport", "ContractVersion", "ModelId", "ModelRevision", "ModelDigest", "ImageDigest")
                VALUES ({reservationId}, {selectedDeployment.EndpointReference}, {selectedDeployment.Transport}, {selectedDeployment.ContractVersion}, {selectedDeployment.ModelId}, {selectedDeployment.ModelRevision}, {selectedDeployment.ModelDigest}, {selectedDeployment.ImageDigest})
                """, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new("claimed", claim.ClaimId);
    }
}
