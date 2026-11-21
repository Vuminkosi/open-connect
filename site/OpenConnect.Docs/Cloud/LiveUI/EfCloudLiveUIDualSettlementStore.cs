using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

/// <summary>Settles a consumed LiveUI plan and bounded token usage atomically.</summary>
public sealed class EfCloudLiveUIDualSettlementStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider)
    : ICloudLiveUIDualSettlementStore
{
    private sealed class TokenRow { public Guid TokenBucketId { get; set; } public long Amount { get; set; } public long? Actual { get; set; } public DateTimeOffset? FinalizedAt { get; set; } }
    private sealed class UsageRow { public Guid ClaimId { get; set; } public Guid DeploymentId { get; set; } public long InputTokens { get; set; } public long OutputTokens { get; set; } }

    public Task<string> SettleAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken) =>
        ApplyAsync(reservationId, claimId, deploymentId, usage, false, cancellationToken);

    public Task<string> FailAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, CancellationToken cancellationToken) =>
        ApplyAsync(reservationId, claimId, deploymentId, usage, true, cancellationToken);

    private async Task<string> ApplyAsync(Guid reservationId, Guid claimId, Guid deploymentId, CloudLiveUITokenUsage usage, bool failed, CancellationToken cancellationToken)
    {
        var actual = checked(usage.InputTokens + usage.OutputTokens);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservation = await db.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {reservationId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (reservation is null || reservation.Amount != 1 ||
            !await db.ExecutionClaims.AsNoTracking().AnyAsync(item => item.ReservationId == reservationId && item.ClaimId == claimId &&
                item.DeploymentId == deploymentId && item.TenantId == reservation.TenantId && item.ApplicationId == reservation.ApplicationId, cancellationToken) ||
            !await db.ExecutionConsumptions.AsNoTracking().AnyAsync(item => item.ReservationId == reservationId && item.ClaimId == claimId && item.DeploymentId == deploymentId, cancellationToken))
            return "unavailable";
        var token = await db.Database.SqlQuery<TokenRow>($"""
            SELECT "TokenBucketId", "Amount", "Actual", "FinalizedAt" FROM cloud.liveui_token_reservations
            WHERE "PlanReservationId" = {reservationId} AND "TenantId" = {reservation.TenantId} AND "ApplicationId" = {reservation.ApplicationId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (token is null) return "unavailable";
        if (reservation.FinalizedAt is not null || token.FinalizedAt is not null)
        {
            var observed = await db.Database.SqlQuery<UsageRow>($"""
                SELECT "ClaimId", "DeploymentId", "InputTokens", "OutputTokens" FROM cloud.liveui_token_observations WHERE "PlanReservationId" = {reservationId}
                """).SingleOrDefaultAsync(cancellationToken);
            var completed = await db.ExecutionCompletions.AsNoTracking().AnyAsync(item => item.ReservationId == reservationId && item.ClaimId == claimId && item.Outcome == "completed", cancellationToken);
            var failure = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value" FROM cloud.liveui_execution_failures
                WHERE "PlanReservationId" = {reservationId} AND "ClaimId" = {claimId} AND "DeploymentId" = {deploymentId}
                """).SingleAsync(cancellationToken);
            return reservation.FinalizedAt is not null && reservation.Actual == 1 && token.FinalizedAt is not null && token.Actual == actual &&
                observed is not null && observed.ClaimId == claimId && observed.DeploymentId == deploymentId &&
                observed.InputTokens == usage.InputTokens && observed.OutputTokens == usage.OutputTokens &&
                (failed ? failure == 1 && !completed : completed && failure == 0) ? (failed ? "already_failed" : "already_completed") : "conflict";
        }
        if (actual > token.Amount) return "overage_requires_reconciliation";
        var buckets = await db.QuotaBuckets.FromSqlInterpolated($"""
            SELECT * FROM cloud.quota_buckets WHERE "TenantId" = {reservation.TenantId} AND "ApplicationId" = {reservation.ApplicationId}
              AND "Id" IN ({reservation.BucketId}, {token.TokenBucketId}) ORDER BY "Id" FOR UPDATE
            """).AsNoTracking().ToArrayAsync(cancellationToken);
        if (buckets.Length != 2 || !buckets.Any(item => item.Id == reservation.BucketId && item.CapabilityId == "inference.liveui" && item.Unit == "plan" && item.Reserved >= 1) ||
            !buckets.Any(item => item.Id == token.TokenBucketId && item.CapabilityId == "inference.liveui" && item.Unit == "token" && item.Reserved >= token.Amount)) return "unavailable";
        var now = timeProvider.GetUtcNow();
        if (failed)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.liveui_execution_failures ("PlanReservationId", "ClaimId", "DeploymentId", "FailedAt")
                VALUES ({reservationId}, {claimId}, {deploymentId}, {now})
                """, cancellationToken);
        else
        {
            db.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = reservationId, ClaimId = claimId, Outcome = "completed", CompletedAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.liveui_token_observations ("PlanReservationId", "ClaimId", "DeploymentId", "InputTokens", "OutputTokens", "ObservedAt")
            VALUES ({reservationId}, {claimId}, {deploymentId}, {usage.InputTokens}, {usage.OutputTokens}, {now})
            """, cancellationToken);
        var tokenFinalized = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cloud.liveui_token_reservations SET "Actual" = {actual}, "FinalizedAt" = {now}
            WHERE "PlanReservationId" = {reservationId} AND "FinalizedAt" IS NULL
            """, cancellationToken);
        var planFinalized = await db.QuotaReservations.Where(item => item.Id == reservationId && item.FinalizedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Actual, 1L).SetProperty(item => item.FinalizedAt, now), cancellationToken);
        var tokenUpdated = await db.QuotaBuckets.Where(item => item.Id == token.TokenBucketId && item.TenantId == reservation.TenantId && item.ApplicationId == reservation.ApplicationId && item.Reserved >= token.Amount)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Reserved, item => item.Reserved - token.Amount).SetProperty(item => item.Consumed, item => item.Consumed + actual), cancellationToken);
        var planUpdated = await db.QuotaBuckets.Where(item => item.Id == reservation.BucketId && item.TenantId == reservation.TenantId && item.ApplicationId == reservation.ApplicationId && item.Reserved >= 1)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Reserved, item => item.Reserved - 1).SetProperty(item => item.Consumed, item => item.Consumed + 1), cancellationToken);
        if (tokenFinalized != 1 || planFinalized != 1 || tokenUpdated != 1 || planUpdated != 1) return "unavailable";
        await transaction.CommitAsync(cancellationToken);
        return failed ? "failed" : "completed";
    }
}
