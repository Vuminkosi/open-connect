using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudVoiceSettlementStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudVoiceSettlementStore
{
    public async Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (reservation is null || reservation.Amount is < 1 or > 2000 ||
            !await db.ExecutionClaims.AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId &&
                x.DeploymentId == deploymentId && x.TenantId == reservation.TenantId && x.ApplicationId == reservation.ApplicationId, cancellationToken) ||
            !await db.ExecutionConsumptions.AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.DeploymentId == deploymentId, cancellationToken) ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == reservation.TenantId && x.ApplicationId == reservation.ApplicationId &&
                x.CapabilityId == "voice.synthesize" && x.Unit == "character", cancellationToken))
            return "unavailable";
        if (reservation.FinalizedAt is not null)
        {
            var completed = await db.ExecutionCompletions.AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.Outcome == "completed", cancellationToken);
            return completed && reservation.Actual == reservation.Amount ? "already_completed" : "conflict";
        }
        var now = timeProvider.GetUtcNow();
        db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = reservationId, ClaimId = claimId, Outcome = "completed", CompletedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        var finalized = await db.QuotaReservations.Where(x => x.Id == reservationId && x.FinalizedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, reservation.Amount).SetProperty(y => y.FinalizedAt, now), cancellationToken);
        if (finalized != 1)
            return "unavailable";
        var updated = await db.QuotaBuckets.Where(x => x.Id == reservation.BucketId && x.TenantId == reservation.TenantId &&
                x.ApplicationId == reservation.ApplicationId && x.Reserved >= reservation.Amount)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - reservation.Amount)
                .SetProperty(y => y.Consumed, y => y.Consumed + reservation.Amount), cancellationToken);
        if (updated != 1)
            return "unavailable";
        await transaction.CommitAsync(cancellationToken);
        return "completed";
    }
}
