using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000200_GuardLiveUIDualSettlement")]
public sealed class GuardLiveUIDualSettlement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.liveui_token_reservations, cloud.execution_consumptions, cloud.execution_completions IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE cloud.liveui_token_observations (
            "PlanReservationId" uuid PRIMARY KEY REFERENCES cloud.liveui_token_reservations ("PlanReservationId") ON DELETE RESTRICT,
            "ClaimId" uuid NOT NULL,
            "DeploymentId" uuid NOT NULL,
            "InputTokens" bigint NOT NULL CHECK ("InputTokens" >= 0),
            "OutputTokens" bigint NOT NULL CHECK ("OutputTokens" >= 0),
            "ObservedAt" timestamptz NOT NULL,
            CHECK ("InputTokens"::numeric + "OutputTokens"::numeric <= 32768)
        );
        CREATE FUNCTION cloud.validate_liveui_token_observation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions c
                JOIN cloud.liveui_token_reservations r ON r."PlanReservationId" = c."ReservationId"
                WHERE c."ReservationId" = NEW."PlanReservationId" AND c."ClaimId" = NEW."ClaimId"
                  AND c."DeploymentId" = NEW."DeploymentId" AND r."FinalizedAt" IS NULL
                  AND NEW."InputTokens" + NEW."OutputTokens" <= r."Amount") THEN
                RAISE EXCEPTION 'LiveUI token observation does not match consumed quota' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER validate_liveui_token_observation BEFORE INSERT ON cloud.liveui_token_observations
            FOR EACH ROW EXECUTE FUNCTION cloud.validate_liveui_token_observation();
        CREATE TRIGGER liveui_token_observation_append_only BEFORE UPDATE OR DELETE ON cloud.liveui_token_observations
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_completion_mutation();
        CREATE TRIGGER liveui_token_observation_no_truncate BEFORE TRUNCATE ON cloud.liveui_token_observations
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_completion_mutation();
        CREATE FUNCTION cloud.guard_liveui_token_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."FinalizedAt" IS NULL AND NEW."FinalizedAt" IS NOT NULL THEN
                IF NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments a WHERE a."ReservationId" = OLD."PlanReservationId")
                    AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions c WHERE c."ReservationId" = OLD."PlanReservationId") THEN RETURN NEW; END IF;
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
        CREATE TRIGGER guard_liveui_token_finalization BEFORE UPDATE ON cloud.liveui_token_reservations
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_liveui_token_finalization();
        CREATE FUNCTION cloud.guard_liveui_dual_plan_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."FinalizedAt" IS NULL AND NEW."FinalizedAt" IS NOT NULL AND EXISTS (
                SELECT 1 FROM cloud.liveui_token_reservations t WHERE t."PlanReservationId" = OLD."Id") THEN
                IF NOT EXISTS (SELECT 1 FROM cloud.liveui_token_reservations t WHERE t."PlanReservationId" = OLD."Id" AND t."FinalizedAt" IS NOT NULL) THEN
                    RAISE EXCEPTION 'LiveUI plan finalization requires token finalization' USING ERRCODE = '23514';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER guard_liveui_dual_plan_finalization BEFORE UPDATE ON cloud.quota_reservations
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_liveui_dual_plan_finalization();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("LiveUI token observations and settlement fences must be retained during rollback.");
}
