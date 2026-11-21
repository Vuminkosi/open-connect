using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

/// <summary>Finds expired consumed claims with durable usage and reconciles them as failed.</summary>
public sealed class CloudLiveUIObservedRecovery(IDbContextFactory<CloudControlPlaneDbContext> factory,
    CloudLiveUIDualSettlement settlement, TimeProvider timeProvider)
{
    private sealed class Candidate
    {
        public Guid ReservationId { get; set; }
        public Guid ClaimId { get; set; }
        public Guid DeploymentId { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }

    public async Task<int> RecoverExpiredAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("LiveUI observed recovery requires all control-plane migrations.");
        var cutoff = timeProvider.GetUtcNow().AddMinutes(-5);
        var candidates = await db.Database.SqlQuery<Candidate>($"""
            SELECT r."Id" AS "ReservationId", o."ClaimId", o."DeploymentId", o."InputTokens", o."OutputTokens"
            FROM cloud.quota_reservations r
            JOIN cloud.liveui_token_reservations t ON t."PlanReservationId" = r."Id"
            JOIN cloud.liveui_token_observations o ON o."PlanReservationId" = r."Id"
            JOIN cloud.execution_consumptions c ON c."ReservationId" = r."Id" AND c."ClaimId" = o."ClaimId" AND c."DeploymentId" = o."DeploymentId"
            WHERE r."FinalizedAt" IS NULL AND t."FinalizedAt" IS NULL AND r."ExpiresAt" <= {cutoff}
              AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
              AND NOT EXISTS (SELECT 1 FROM cloud.execution_completions x WHERE x."ReservationId" = r."Id")
              AND NOT EXISTS (SELECT 1 FROM cloud.liveui_execution_failures f WHERE f."PlanReservationId" = r."Id")
            ORDER BY r."ExpiresAt", r."Id" LIMIT 100
            """).ToArrayAsync(cancellationToken);
        var recovered = 0;
        foreach (var candidate in candidates)
        {
            var result = await settlement.RecoverAsync(candidate.ReservationId, candidate.ClaimId, candidate.DeploymentId,
                new(candidate.InputTokens, candidate.OutputTokens), cancellationToken);
            if (result == "failed") recovered++;
        }
        return recovered;
    }
}
