using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudExecutionCompletionStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudExecutionCompletionStore
{
    public async Task<CloudExecutionCompletionResult> CompleteClassificationAsync(Guid tenantId, Guid applicationId, Guid reservationId, Guid claimId, string outcome, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} AND \"TenantId\" = {tenantId} AND \"ApplicationId\" = {applicationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (reservation is null || reservation.Amount != 1 || !await db.ExecutionClaims.AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.TenantId == tenantId && x.ApplicationId == applicationId, cancellationToken) ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.CapabilityId == "classification.classify" && x.Unit == "request", cancellationToken))
            return new("unavailable");
        if (reservation.FinalizedAt is not null)
        {
            var completed = await db.ExecutionCompletions.AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.Outcome == outcome, cancellationToken);
            return new(completed && reservation.Actual == 1 ? "already_completed" : "conflict");
        }
        var now = timeProvider.GetUtcNow();
        db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = reservationId, ClaimId = claimId, Outcome = outcome, CompletedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        var finalized = await db.QuotaReservations.Where(x => x.Id == reservationId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.FinalizedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 1L).SetProperty(y => y.FinalizedAt, now), cancellationToken);
        if (finalized != 1) return new("unavailable");
        var updated = await db.QuotaBuckets.Where(x => x.Id == reservation.BucketId && x.TenantId == tenantId && x.ApplicationId == applicationId && x.Reserved >= 1)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - 1).SetProperty(y => y.Consumed, y => y.Consumed + 1), cancellationToken);
        if (updated != 1) return new("unavailable");
        await transaction.CommitAsync(cancellationToken);
        return new("completed");
    }
}
