using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000400_RecoverUnclaimedLiveUIQuota")]
public sealed class RecoverUnclaimedLiveUIQuota : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.liveui_token_reservations, cloud.execution_claims,
            cloud.execution_consumptions, cloud.execution_completions IN SHARE ROW EXCLUSIVE MODE;
        CREATE OR REPLACE FUNCTION cloud.guard_liveui_token_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."FinalizedAt" IS NULL AND NEW."FinalizedAt" IS NOT NULL THEN
                PERFORM 1 FROM cloud.quota_reservations r WHERE r."Id" = OLD."PlanReservationId" FOR UPDATE;
                IF NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments a WHERE a."ReservationId" = OLD."PlanReservationId")
                    AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions c WHERE c."ReservationId" = OLD."PlanReservationId") THEN RETURN NEW; END IF;
                IF NEW."Actual" = 0 AND EXISTS (
                    SELECT 1 FROM cloud.quota_reservations r WHERE r."Id" = OLD."PlanReservationId"
                      AND r."FinalizedAt" IS NULL AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
                      AND NOT EXISTS (SELECT 1 FROM cloud.execution_claims c WHERE c."ReservationId" = r."Id")
                    ) THEN RETURN NEW; END IF;
                IF NOT EXISTS (
                    SELECT 1 FROM cloud.liveui_token_observations o
                    JOIN cloud.execution_consumptions c ON c."ReservationId" = o."PlanReservationId" AND c."ClaimId" = o."ClaimId" AND c."DeploymentId" = o."DeploymentId"
                    JOIN cloud.execution_completions x ON x."ReservationId" = c."ReservationId" AND x."ClaimId" = c."ClaimId" AND x."Outcome" = 'completed'
                    WHERE o."PlanReservationId" = OLD."PlanReservationId" AND NEW."Actual" = o."InputTokens" + o."OutputTokens") THEN
                    RAISE EXCEPTION 'LiveUI token finalization requires matching consumed usage' USING ERRCODE = '23514';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE FUNCTION cloud.require_liveui_plan_finalization_at_commit() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."FinalizedAt" IS NULL AND NEW."FinalizedAt" IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM cloud.quota_reservations r WHERE r."Id" = NEW."PlanReservationId" AND r."FinalizedAt" IS NOT NULL) THEN
                RAISE EXCEPTION 'LiveUI token finalization requires plan finalization' USING ERRCODE = '23514';
            END IF;
            RETURN NULL;
        END;
        $$;
        CREATE CONSTRAINT TRIGGER require_liveui_plan_finalization_at_commit
            AFTER UPDATE ON cloud.liveui_token_reservations DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION cloud.require_liveui_plan_finalization_at_commit();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain LiveUI quota recovery fences during application rollback.");
}
