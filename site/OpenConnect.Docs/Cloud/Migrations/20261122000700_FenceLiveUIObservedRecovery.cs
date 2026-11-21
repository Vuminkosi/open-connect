using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000700_FenceLiveUIObservedRecovery")]
public sealed class FenceLiveUIObservedRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.liveui_execution_failures, cloud.liveui_token_observations IN SHARE ROW EXCLUSIVE MODE;
        ALTER TABLE cloud.liveui_execution_failures ADD COLUMN "Source" text NOT NULL DEFAULT 'worker'
            CHECK ("Source" IN ('worker', 'observed_recovery'));
        CREATE OR REPLACE FUNCTION cloud.validate_liveui_execution_failure() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM 1 FROM cloud.quota_reservations r WHERE r."Id" = NEW."PlanReservationId" AND r."FinalizedAt" IS NULL FOR UPDATE;
            IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions c
                WHERE c."ReservationId" = NEW."PlanReservationId" AND c."ClaimId" = NEW."ClaimId" AND c."DeploymentId" = NEW."DeploymentId")
                OR EXISTS (SELECT 1 FROM cloud.execution_completions x WHERE x."ReservationId" = NEW."PlanReservationId") THEN
                RAISE EXCEPTION 'LiveUI failure requires an unfinalized consumed claim' USING ERRCODE = '23514';
            END IF;
            IF NEW."Source" = 'observed_recovery' AND NOT EXISTS (
                SELECT 1 FROM cloud.quota_reservations r JOIN cloud.liveui_token_observations o ON o."PlanReservationId" = r."Id"
                WHERE r."Id" = NEW."PlanReservationId" AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
                  AND o."ClaimId" = NEW."ClaimId" AND o."DeploymentId" = NEW."DeploymentId") THEN
                RAISE EXCEPTION 'LiveUI recovery requires expired observed usage' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain LiveUI recovery provenance during application rollback.");
}
