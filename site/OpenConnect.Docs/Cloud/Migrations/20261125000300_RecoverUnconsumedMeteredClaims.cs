using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261125000300_RecoverUnconsumedMeteredClaims")]
public sealed class RecoverUnconsumedMeteredClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.liveui_token_reservations, cloud.execution_claims,
            cloud.execution_consumptions, cloud.execution_completions, cloud.execution_abandonments IN SHARE ROW EXCLUSIVE MODE;
        CREATE OR REPLACE FUNCTION cloud.validate_execution_abandonment() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM 1 FROM cloud.quota_reservations r
                JOIN cloud.execution_claims c ON c."ReservationId" = r."Id" AND c."ClaimId" = NEW."ClaimId"
                JOIN cloud.quota_buckets b ON b."Id" = r."BucketId" AND b."TenantId" = r."TenantId" AND b."ApplicationId" = r."ApplicationId"
                WHERE r."Id" = NEW."ReservationId" AND r."FinalizedAt" IS NULL AND r."Amount" > 0
                  AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
                  AND ((b."CapabilityId" IN ('classification.classify', 'analytics.analyze', 'knowledge.embed') AND b."Unit" = 'request' AND r."Amount" = 1) OR
                       (b."CapabilityId" = 'inference.chat' AND b."Unit" = 'token') OR
                       (b."CapabilityId" IN ('voice.synthesize', 'translation.translate') AND b."Unit" = 'character') OR
                       (b."CapabilityId" = 'inference.liveui' AND b."Unit" = 'plan' AND r."Amount" = 1 AND EXISTS (
                           SELECT 1 FROM cloud.liveui_token_reservations t WHERE t."PlanReservationId" = r."Id"
                             AND t."TenantId" = r."TenantId" AND t."ApplicationId" = r."ApplicationId" AND t."FinalizedAt" IS NULL)))
                  AND c."DeploymentId" IS NOT NULL AND c."Region" IS NOT NULL AND octet_length(c."WorkerPayloadFingerprint") = 32
                FOR UPDATE OF r;
            IF NOT FOUND OR EXISTS (SELECT 1 FROM cloud.execution_consumptions WHERE "ReservationId" = NEW."ReservationId")
                OR EXISTS (SELECT 1 FROM cloud.execution_completions WHERE "ReservationId" = NEW."ReservationId") THEN
                RAISE EXCEPTION 'claim is not eligible for abandonment' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain metered claim recovery protections during application rollback.");
}
