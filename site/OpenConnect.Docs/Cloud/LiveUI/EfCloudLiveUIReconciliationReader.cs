using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

/// <summary>Reads only unresolved metadata; request content and credentials are excluded.</summary>
public sealed class EfCloudLiveUIReconciliationReader(IDbContextFactory<CloudControlPlaneDbContext> factory) : ICloudLiveUIReconciliationReader
{
    public async Task<IReadOnlyList<CloudLiveUIUnknownUsage>> ReadUnknownAsync(CloudLiveUIReconciliationCursor? after, int count,
        DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var cursorTime = after?.ExpiresAt;
        var cursorId = after?.ReservationId;
        return await db.Database.SqlQuery<CloudLiveUIUnknownUsage>($"""
            SELECT r."Id" AS "ReservationId", r."TenantId", r."ApplicationId", r."RequestId", c."DeploymentId",
                   r."ExpiresAt", t."Amount" AS "ReservedTokens"
            FROM cloud.quota_reservations r
            JOIN cloud.liveui_token_reservations t ON t."PlanReservationId" = r."Id" AND t."TenantId" = r."TenantId" AND t."ApplicationId" = r."ApplicationId"
            JOIN cloud.execution_consumptions c ON c."ReservationId" = r."Id"
            WHERE r."FinalizedAt" IS NULL AND t."FinalizedAt" IS NULL
              AND r."ExpiresAt" <= {cutoff} AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
              AND NOT EXISTS (SELECT 1 FROM cloud.liveui_token_observations o WHERE o."PlanReservationId" = r."Id")
              AND NOT EXISTS (SELECT 1 FROM cloud.liveui_execution_failures f WHERE f."PlanReservationId" = r."Id")
              AND NOT EXISTS (SELECT 1 FROM cloud.execution_completions x WHERE x."ReservationId" = r."Id")
              AND (CAST({cursorTime} AS timestamptz) IS NULL OR (r."ExpiresAt", r."Id") > ({cursorTime}, {cursorId}))
            ORDER BY r."ExpiresAt", r."Id" LIMIT {count}
            """).ToArrayAsync(cancellationToken);
    }
}
