using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudExecutionConsumptionStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudExecutionConsumptionStore
{
    public async Task<string> ConsumeAsync(Guid reservationId, Guid claimId, Guid deploymentId, byte[] workerPayloadFingerprint, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var deployment = await db.WorkerDeployments.FromSqlInterpolated($"SELECT * FROM cloud.worker_deployments WHERE \"Id\" = {deploymentId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (deployment is null || !deployment.Enabled || deployment.CapabilityId != "classification.classify") return "unavailable";
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var claim = await db.ExecutionClaims.AsNoTracking().SingleOrDefaultAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.DeploymentId == deploymentId && x.Region == deployment.Region, cancellationToken);
        if (reservation is null || reservation.FinalizedAt is not null || reservation.ExpiresAt <= now ||
            claim?.WorkerPayloadFingerprint is not { Length: 32 } || !CryptographicOperations.FixedTimeEquals(claim.WorkerPayloadFingerprint, workerPayloadFingerprint) ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == reservation.TenantId && x.ApplicationId == reservation.ApplicationId && x.CapabilityId == "classification.classify" && x.Unit == "request", cancellationToken))
            return "unavailable";
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_consumptions (\"ReservationId\", \"ClaimId\", \"DeploymentId\", \"ConsumedAt\") VALUES ({reservationId}, {claimId}, {deploymentId}, {now}) ON CONFLICT (\"ReservationId\") DO NOTHING", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1 ? "consumed" : "already_consumed";
    }
}
