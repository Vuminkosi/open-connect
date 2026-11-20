using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudInferenceTokenSettlementStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider) : ICloudInferenceTokenSettlementStore
{
    private sealed class UsageRow { public Guid ClaimId { get; set; } public long InputTokens { get; set; } public long OutputTokens { get; set; } }

    public async Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudInferenceTokenUsage usage, CancellationToken cancellationToken)
    {
        var actual = checked(usage.InputTokens + usage.OutputTokens);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (reservation is null || !await db.ExecutionClaims.AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.DeploymentId == deploymentId && x.TenantId == reservation.TenantId && x.ApplicationId == reservation.ApplicationId, cancellationToken) ||
            !await db.ExecutionConsumptions.AsNoTracking().AnyAsync(x => x.ReservationId == reservationId && x.ClaimId == claimId && x.DeploymentId == deploymentId, cancellationToken) ||
            !await db.QuotaBuckets.AnyAsync(x => x.Id == reservation.BucketId && x.TenantId == reservation.TenantId && x.ApplicationId == reservation.ApplicationId && x.CapabilityId == "inference.chat" && x.Unit == "token", cancellationToken)) return "unavailable";
        if (reservation.FinalizedAt is not null)
        {
            var existing = await db.Database.SqlQuery<UsageRow>($"SELECT \"ClaimId\", \"InputTokens\", \"OutputTokens\" FROM cloud.inference_token_observations WHERE \"ReservationId\" = {reservationId}").SingleOrDefaultAsync(cancellationToken);
            return existing is not null && existing.ClaimId == claimId && existing.InputTokens == usage.InputTokens && existing.OutputTokens == usage.OutputTokens && reservation.Actual == actual ? "already_completed" : "conflict";
        }
        if (actual > reservation.Amount) return "overage_requires_reconciliation";
        var now = timeProvider.GetUtcNow();
        db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = reservationId, ClaimId = claimId, Outcome = "completed", CompletedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.inference_token_observations (\"ReservationId\", \"ClaimId\", \"InputTokens\", \"OutputTokens\") VALUES ({reservationId}, {claimId}, {usage.InputTokens}, {usage.OutputTokens})", cancellationToken);
        var finalized = await db.QuotaReservations.Where(x => x.Id == reservationId && x.FinalizedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, actual).SetProperty(y => y.FinalizedAt, now), cancellationToken);
        if (finalized != 1) return "unavailable";
        var updated = await db.QuotaBuckets.Where(x => x.Id == reservation.BucketId && x.TenantId == reservation.TenantId && x.ApplicationId == reservation.ApplicationId && x.Reserved >= reservation.Amount)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - reservation.Amount).SetProperty(y => y.Consumed, y => y.Consumed + actual), cancellationToken);
        if (updated != 1) return "unavailable";
        await transaction.CommitAsync(cancellationToken);
        return "completed";
    }
}
