using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

/// <summary>Releases expired dual reservations only when no execution claim exists.</summary>
public sealed class EfCloudLiveUIUnclaimedRecoveryStore(IDbContextFactory<CloudControlPlaneDbContext> factory, TimeProvider timeProvider)
    : ICloudLiveUIUnclaimedRecoveryStore
{
    private sealed class TokenRow { public Guid TokenBucketId { get; set; } public long Amount { get; set; } }

    public async Task<int> ReleaseAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("LiveUI recovery requires all control-plane migrations.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reservations = await db.QuotaReservations.FromSqlInterpolated($"""
            SELECT r.* FROM cloud.quota_reservations r
            JOIN cloud.liveui_token_reservations t ON t."PlanReservationId" = r."Id"
            JOIN cloud.quota_buckets b ON b."Id" = r."BucketId" AND b."TenantId" = r."TenantId" AND b."ApplicationId" = r."ApplicationId"
            WHERE r."FinalizedAt" IS NULL AND t."FinalizedAt" IS NULL AND r."Amount" = 1
              AND r."ExpiresAt" <= {cutoff} AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
              AND b."CapabilityId" = 'inference.liveui' AND b."Unit" = 'plan'
              AND NOT EXISTS (SELECT 1 FROM cloud.execution_claims c WHERE c."ReservationId" = r."Id")
            ORDER BY r."Id" LIMIT {limit} FOR UPDATE OF r SKIP LOCKED
            """).AsNoTracking().ToArrayAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var reservation in reservations)
        {
            var token = await db.Database.SqlQuery<TokenRow>($"""
                SELECT "TokenBucketId", "Amount" FROM cloud.liveui_token_reservations
                WHERE "PlanReservationId" = {reservation.Id} AND "TenantId" = {reservation.TenantId}
                  AND "ApplicationId" = {reservation.ApplicationId} AND "FinalizedAt" IS NULL FOR UPDATE
                """).SingleOrDefaultAsync(cancellationToken);
            if (token is null)
                throw new InvalidOperationException("LiveUI token reservation is unavailable.");
            var buckets = await db.QuotaBuckets.FromSqlInterpolated($"""
                SELECT * FROM cloud.quota_buckets WHERE "TenantId" = {reservation.TenantId} AND "ApplicationId" = {reservation.ApplicationId}
                  AND "Id" IN ({reservation.BucketId}, {token.TokenBucketId}) ORDER BY "Id" FOR UPDATE
                """).AsNoTracking().ToArrayAsync(cancellationToken);
            if (buckets.Length != 2 || !buckets.Any(b => b.Id == reservation.BucketId && b.Unit == "plan" && b.CapabilityId == "inference.liveui" && b.Reserved >= 1) ||
                !buckets.Any(b => b.Id == token.TokenBucketId && b.Unit == "token" && b.CapabilityId == "inference.liveui" && b.Reserved >= token.Amount))
                throw new InvalidOperationException("LiveUI quota recovery could not preserve the reservation balance.");
            var tokenFinalized = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.liveui_token_reservations SET "Actual" = 0, "FinalizedAt" = {now}
                WHERE "PlanReservationId" = {reservation.Id} AND "FinalizedAt" IS NULL
                """, cancellationToken);
            var planFinalized = await db.QuotaReservations.Where(r => r.Id == reservation.Id && r.FinalizedAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(r => r.Actual, 0L).SetProperty(r => r.FinalizedAt, now), cancellationToken);
            var tokenUpdated = await db.QuotaBuckets.Where(b => b.Id == token.TokenBucketId && b.TenantId == reservation.TenantId &&
                    b.ApplicationId == reservation.ApplicationId && b.Reserved >= token.Amount)
                .ExecuteUpdateAsync(update => update.SetProperty(b => b.Reserved, b => b.Reserved - token.Amount), cancellationToken);
            var planUpdated = await db.QuotaBuckets.Where(b => b.Id == reservation.BucketId && b.TenantId == reservation.TenantId &&
                    b.ApplicationId == reservation.ApplicationId && b.Reserved >= 1)
                .ExecuteUpdateAsync(update => update.SetProperty(b => b.Reserved, b => b.Reserved - 1), cancellationToken);
            if (tokenFinalized != 1 || planFinalized != 1 || tokenUpdated != 1 || planUpdated != 1)
                throw new InvalidOperationException("LiveUI quota recovery could not finalize both reservations.");
        }
        await transaction.CommitAsync(cancellationToken);
        return reservations.Length;
    }
}
