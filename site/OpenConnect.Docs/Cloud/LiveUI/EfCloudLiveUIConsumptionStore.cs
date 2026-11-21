using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudLiveUIConsumptionStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider)
    : ICloudLiveUIConsumptionStore, ICloudLiveUIMeteredConsumptionStore
{
    public Task<CloudLiveUIConsumptionResult> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken) =>
        ConsumeCoreAsync(reservationId, claimId, deploymentId, workerPayloadFingerprint, null, cancellationToken);

    public Task<CloudLiveUIConsumptionResult> ConsumeMeteredAsync(Guid reservationId, Guid claimId, Guid deploymentId,
        byte[] workerPayloadFingerprint, int contextTokens, CancellationToken cancellationToken) =>
        ConsumeCoreAsync(reservationId, claimId, deploymentId, workerPayloadFingerprint, contextTokens, cancellationToken);

    private async Task<CloudLiveUIConsumptionResult> ConsumeCoreAsync(Guid reservationId, Guid claimId, Guid deploymentId,
        byte[] workerPayloadFingerprint, int? contextTokens, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claim = await db.ExecutionClaims.AsNoTracking().SingleOrDefaultAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.DeploymentId == deploymentId, cancellationToken);
        if (claim is null || claim.WorkerPayloadFingerprint is not { Length: 32 } ||
            !CryptographicOperations.FixedTimeEquals(claim.WorkerPayloadFingerprint, workerPayloadFingerprint)) return new("unavailable");
        var credentialId = await db.Database.SqlQuery<Guid>($"SELECT \"CredentialId\" AS \"Value\" FROM cloud.execution_claim_credentials WHERE \"ReservationId\" = {reservationId}")
            .SingleOrDefaultAsync(cancellationToken);
        if (credentialId == Guid.Empty) return new("unavailable");
        var credential = await db.Credentials.FromSqlInterpolated($"SELECT * FROM cloud.credentials WHERE \"Id\" = {credentialId} AND \"TenantId\" = {claim.TenantId} AND \"ApplicationId\" = {claim.ApplicationId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (credential is null || credential.RevokedAt is not null || credential.ExpiresAt is null ||
            !(JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? []).Contains("inference.liveui", StringComparer.Ordinal)) return new("unavailable");
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM cloud.applications WHERE \"Id\" = {claim.ApplicationId} AND \"TenantId\" = {claim.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (application is null || application.Environment != credential.Environment) return new("unavailable");
        var capabilities = await EfCloudClaimCapabilitySnapshot.LockAsync(db, claim.TenantId, claim.ApplicationId, cancellationToken);
        var modelId = await db.Database.SqlQuery<string>($"SELECT \"ModelId\" AS \"Value\" FROM cloud.execution_claim_inference_models WHERE \"ReservationId\" = {reservationId}").SingleOrDefaultAsync(cancellationToken);
        if (!await EfCloudInferenceModelAdmission.LockAllowedAsync(db, claim.TenantId, modelId, cancellationToken)) return new("unavailable");
        var policy = await db.RegionPolicies.FromSqlInterpolated($"SELECT * FROM cloud.region_policies WHERE \"TenantId\" = {claim.TenantId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var catalog = capabilities.Capabilities.SingleOrDefault(x => x.Id == "inference.liveui");
        if (policy is null || catalog is null || !catalog.AllowedRegionsVerified || claim.Region is null ||
            !(JsonSerializer.Deserialize<string[]>(policy.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal) ||
            !(JsonSerializer.Deserialize<string[]>(catalog.AllowedRegions) ?? []).Contains(claim.Region, StringComparer.Ordinal)) return new("unavailable");
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"Id\" = {deploymentId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (deployment is not { Enabled: true, CapabilityId: "inference.liveui", Transport: "https-mtls" } || deployment.Region != claim.Region || deployment.ModelId != modelId)
            return new("unavailable");
        var matchingEvidence = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM cloud.execution_claim_deployment_evidence
                WHERE "ReservationId" = {reservationId} AND "EndpointReference" = {deployment.EndpointReference}
                  AND "Transport" = {deployment.Transport} AND "ContractVersion" = {deployment.ContractVersion}
                  AND "ModelId" = {deployment.ModelId} AND "ModelRevision" = {deployment.ModelRevision}
                  AND "ModelDigest" = {deployment.ModelDigest} AND "ImageDigest" = {deployment.ImageDigest}) AS "Value"
            """).SingleAsync(cancellationToken);
        if (!matchingEvidence) return new("unavailable");
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (credential.ExpiresAt <= now || CloudCapabilityProjection.Application(capabilities, now).FirstOrDefault(x => x.Id == "inference.liveui") is not { ReasonCode: null } ||
            reservation is null || reservation.TenantId != claim.TenantId || reservation.ApplicationId != claim.ApplicationId ||
            reservation.FinalizedAt is not null || reservation.ExpiresAt <= now || reservation.Amount != 1 ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == claim.TenantId && x.ApplicationId == claim.ApplicationId &&
                x.CapabilityId == "inference.liveui" && x.Unit == "plan", cancellationToken)) return new("unavailable");
        if (!CloudRequestFingerprint.IsValidKeyId(reservation.FingerprintKeyId) || reservation.RequestFingerprint is not { Length: 32 }) return new("unavailable");
        var privacyAllowed = await EfCloudLiveUIPrivacyAdmission.IsAllowedAsync(db, claim.TenantId, claim.ApplicationId,
            reservation.RequestId, reservation.FingerprintKeyId!, reservation.RequestFingerprint, cancellationToken);
        if (!privacyAllowed) return new("unavailable");
        if (await db.ExecutionConsumptions.AnyAsync(item => item.ReservationId == reservationId, cancellationToken)) return new("already_consumed");
        if (contextTokens is not null)
        {
            var tokenAmount = await db.Database.SqlQuery<long>($"""
                SELECT "Amount" AS "Value" FROM cloud.liveui_token_reservations WHERE "PlanReservationId" = {reservationId}
                  AND "TenantId" = {claim.TenantId} AND "ApplicationId" = {claim.ApplicationId} AND "FinalizedAt" IS NULL FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken);
            if (contextTokens is < 256 or > 32768 || tokenAmount < contextTokens) return new("unavailable");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_metered_consumption_authorizations ("PlanReservationId", "ClaimId", "DeploymentId", "ContextTokens", "AuthorizedAt")
                VALUES ({reservationId}, {claimId}, {deploymentId}, {contextTokens.Value}, {now})
                """, cancellationToken);
        }
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_consumptions (\"ReservationId\", \"ClaimId\", \"DeploymentId\", \"ConsumedAt\") VALUES ({reservationId}, {claimId}, {deploymentId}, {now}) ON CONFLICT (\"ReservationId\") DO NOTHING", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1 ? new("consumed", modelId, deployment.ModelRevision, deployment.ModelDigest) : new("already_consumed");
    }
}
