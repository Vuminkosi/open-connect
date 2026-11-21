using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000600_AddLiveUIFailedUsage")]
public sealed class AddLiveUIFailedUsage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.liveui_token_reservations, cloud.execution_consumptions,
            cloud.execution_completions, cloud.liveui_token_observations IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE cloud.liveui_execution_failures (
            "PlanReservationId" uuid PRIMARY KEY REFERENCES cloud.liveui_token_reservations ("PlanReservationId") ON DELETE RESTRICT,
            "ClaimId" uuid NOT NULL,
            "DeploymentId" uuid NOT NULL,
            "FailedAt" timestamptz NOT NULL
        );
        CREATE FUNCTION cloud.validate_liveui_execution_failure() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM 1 FROM cloud.quota_reservations r WHERE r."Id" = NEW."PlanReservationId" AND r."FinalizedAt" IS NULL FOR UPDATE;
            IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions c
                WHERE c."ReservationId" = NEW."PlanReservationId" AND c."ClaimId" = NEW."ClaimId" AND c."DeploymentId" = NEW."DeploymentId")
                OR EXISTS (SELECT 1 FROM cloud.execution_completions x WHERE x."ReservationId" = NEW."PlanReservationId") THEN
                RAISE EXCEPTION 'LiveUI failure requires an unfinalized consumed claim' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER validate_liveui_execution_failure BEFORE INSERT ON cloud.liveui_execution_failures
            FOR EACH ROW EXECUTE FUNCTION cloud.validate_liveui_execution_failure();
        CREATE TRIGGER liveui_execution_failure_append_only BEFORE UPDATE OR DELETE ON cloud.liveui_execution_failures
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_completion_mutation();
        CREATE TRIGGER liveui_execution_failure_no_truncate BEFORE TRUNCATE ON cloud.liveui_execution_failures
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_completion_mutation();
        CREATE OR REPLACE FUNCTION cloud.guard_execution_consumption_reservation() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE finalized_at timestamptz;
        BEGIN
            SELECT "FinalizedAt" INTO finalized_at FROM cloud.quota_reservations WHERE "Id" = NEW."ReservationId" FOR UPDATE;
            IF NOT FOUND OR finalized_at IS NOT NULL OR EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE "ReservationId" = NEW."ReservationId")
                OR EXISTS (SELECT 1 FROM cloud.liveui_execution_failures WHERE "PlanReservationId" = NEW."ReservationId") THEN
                RAISE EXCEPTION 'reservation unavailable for consumption' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE OR REPLACE FUNCTION cloud.guard_liveui_token_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."FinalizedAt" IS NULL AND NEW."FinalizedAt" IS NOT NULL THEN
                PERFORM 1 FROM cloud.quota_reservations r WHERE r."Id" = OLD."PlanReservationId" FOR UPDATE;
                IF NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments a WHERE a."ReservationId" = OLD."PlanReservationId")
                    AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions c WHERE c."ReservationId" = OLD."PlanReservationId") THEN RETURN NEW; END IF;
                IF NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.quota_reservations r WHERE r."Id" = OLD."PlanReservationId"
                    AND r."FinalizedAt" IS NULL AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
                    AND NOT EXISTS (SELECT 1 FROM cloud.execution_claims c WHERE c."ReservationId" = r."Id")) THEN RETURN NEW; END IF;
                IF NOT EXISTS (SELECT 1 FROM cloud.liveui_token_observations o
                    JOIN cloud.execution_consumptions c ON c."ReservationId" = o."PlanReservationId" AND c."ClaimId" = o."ClaimId" AND c."DeploymentId" = o."DeploymentId"
                    WHERE o."PlanReservationId" = OLD."PlanReservationId" AND NEW."Actual" = o."InputTokens" + o."OutputTokens"
                      AND (EXISTS (SELECT 1 FROM cloud.execution_completions x WHERE x."ReservationId" = c."ReservationId" AND x."ClaimId" = c."ClaimId" AND x."Outcome" = 'completed')
                        OR EXISTS (SELECT 1 FROM cloud.liveui_execution_failures f WHERE f."PlanReservationId" = c."ReservationId" AND f."ClaimId" = c."ClaimId" AND f."DeploymentId" = c."DeploymentId"))) THEN
                    RAISE EXCEPTION 'LiveUI token finalization requires matching consumed usage' USING ERRCODE = '23514';
                END IF;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE OR REPLACE FUNCTION cloud.guard_claimed_reservation_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE inference_bucket boolean;
        DECLARE liveui_bucket boolean;
        BEGIN
            IF NEW."FinalizedAt" IS NULL OR OLD."FinalizedAt" IS NOT NULL OR
               NOT EXISTS (SELECT 1 FROM cloud.execution_claims WHERE "ReservationId" = OLD."Id") THEN RETURN NEW; END IF;
            IF NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE "ReservationId" = OLD."Id")
                AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions WHERE "ReservationId" = OLD."Id") THEN RETURN NEW; END IF;
            IF OLD."FinalizedAt" IS NULL AND NEW."FinalizedAt" IS NOT NULL AND NEW."Actual" = 1 AND OLD."Amount" = 1
                AND EXISTS (SELECT 1 FROM cloud.liveui_execution_failures f
                    JOIN cloud.liveui_token_observations o ON o."PlanReservationId" = f."PlanReservationId" AND o."ClaimId" = f."ClaimId" AND o."DeploymentId" = f."DeploymentId"
                    JOIN cloud.liveui_token_reservations t ON t."PlanReservationId" = f."PlanReservationId" AND t."FinalizedAt" IS NOT NULL AND t."Actual" = o."InputTokens" + o."OutputTokens"
                    WHERE f."PlanReservationId" = OLD."Id") THEN
                RETURN NEW;
            END IF;
            SELECT EXISTS (SELECT 1 FROM cloud.quota_buckets b WHERE b."Id" = OLD."BucketId" AND b."CapabilityId" = 'inference.chat') INTO inference_bucket;
            SELECT EXISTS (SELECT 1 FROM cloud.quota_buckets b WHERE b."Id" = OLD."BucketId" AND b."CapabilityId" = 'inference.liveui') INTO liveui_bucket;
            IF inference_bucket THEN
                IF NEW."Actual" IS NULL OR NEW."Actual" > OLD."Amount" OR NOT EXISTS (
                    SELECT 1 FROM cloud.inference_token_observations t
                    JOIN cloud.execution_consumptions u ON u."ReservationId" = t."ReservationId" AND u."ClaimId" = t."ClaimId"
                    JOIN cloud.execution_claims x ON x."ReservationId" = u."ReservationId" AND x."ClaimId" = u."ClaimId" AND x."DeploymentId" = u."DeploymentId"
                    JOIN cloud.execution_claim_inference_models m ON m."ReservationId" = x."ReservationId"
                    JOIN cloud.execution_completions c ON c."ReservationId" = x."ReservationId" AND c."ClaimId" = x."ClaimId"
                    JOIN cloud.quota_buckets b ON b."Id" = OLD."BucketId" AND b."TenantId" = OLD."TenantId" AND b."ApplicationId" = OLD."ApplicationId"
                    WHERE t."ReservationId" = OLD."Id" AND b."Unit" = 'token' AND NEW."Actual" = t."InputTokens" + t."OutputTokens") THEN
                    RAISE EXCEPTION 'inference settlement requires matching token evidence' USING ERRCODE = '23514';
                END IF;
            ELSIF liveui_bucket THEN
                IF NEW."Actual" IS DISTINCT FROM 1 OR OLD."Amount" <> 1 OR NOT EXISTS (
                    SELECT 1 FROM cloud.execution_consumptions u
                    JOIN cloud.execution_claims x ON x."ReservationId" = u."ReservationId" AND x."ClaimId" = u."ClaimId" AND x."DeploymentId" = u."DeploymentId"
                    JOIN cloud.execution_completions c ON c."ReservationId" = x."ReservationId" AND c."ClaimId" = x."ClaimId"
                    JOIN cloud.quota_buckets b ON b."Id" = OLD."BucketId" AND b."TenantId" = OLD."TenantId" AND b."ApplicationId" = OLD."ApplicationId"
                    WHERE u."ReservationId" = OLD."Id" AND b."Unit" = 'plan') THEN
                    RAISE EXCEPTION 'LiveUI settlement requires matching consumption' USING ERRCODE = '23514';
                END IF;
            ELSIF NEW."Actual" <> OLD."Amount" OR NOT EXISTS (
                SELECT 1 FROM cloud.execution_completions c JOIN cloud.execution_claims x
                  ON x."ReservationId" = c."ReservationId" AND x."ClaimId" = c."ClaimId" WHERE c."ReservationId" = OLD."Id") THEN
                RAISE EXCEPTION 'claimed execution requires matching completion or abandonment' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain LiveUI failed-usage evidence during application rollback.");
}
