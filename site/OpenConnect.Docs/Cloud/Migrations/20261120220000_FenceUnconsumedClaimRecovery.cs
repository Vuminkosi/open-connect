using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261120220000_FenceUnconsumedClaimRecovery")]
public sealed class FenceUnconsumedClaimRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.execution_claims, cloud.execution_consumptions, cloud.execution_completions IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE cloud.execution_abandonments (
            "ReservationId" uuid PRIMARY KEY REFERENCES cloud.execution_claims ("ReservationId") ON DELETE RESTRICT,
            "ClaimId" uuid NOT NULL,
            "AbandonedAt" timestamp with time zone NOT NULL,
            "Reason" text NOT NULL CHECK ("Reason" = 'expired_unconsumed')
        );
        CREATE FUNCTION cloud.validate_execution_abandonment() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM 1 FROM cloud.quota_reservations r
                JOIN cloud.execution_claims c ON c."ReservationId" = r."Id" AND c."ClaimId" = NEW."ClaimId"
                JOIN cloud.quota_buckets b ON b."Id" = r."BucketId" AND b."TenantId" = r."TenantId" AND b."ApplicationId" = r."ApplicationId"
                WHERE r."Id" = NEW."ReservationId" AND r."FinalizedAt" IS NULL AND r."Amount" = 1
                  AND r."ExpiresAt" <= clock_timestamp() - interval '5 minutes'
                  AND b."CapabilityId" = 'classification.classify' AND b."Unit" = 'request'
                  AND c."DeploymentId" IS NOT NULL AND c."Region" IS NOT NULL
                  AND octet_length(c."WorkerPayloadFingerprint") = 32
                FOR UPDATE OF r;
            IF NOT FOUND OR EXISTS (SELECT 1 FROM cloud.execution_consumptions WHERE "ReservationId" = NEW."ReservationId")
                OR EXISTS (SELECT 1 FROM cloud.execution_completions WHERE "ReservationId" = NEW."ReservationId") THEN
                RAISE EXCEPTION 'claim is not eligible for abandonment' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_validate_execution_abandonment BEFORE INSERT ON cloud.execution_abandonments
            FOR EACH ROW EXECUTE FUNCTION cloud.validate_execution_abandonment();
        CREATE FUNCTION cloud.reject_execution_abandonment_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'cloud execution abandonments are append-only'; END;
        $$;
        CREATE TRIGGER cloud_execution_abandonment_append_only BEFORE UPDATE OR DELETE ON cloud.execution_abandonments
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_abandonment_mutation();
        CREATE TRIGGER cloud_execution_abandonment_no_truncate BEFORE TRUNCATE ON cloud.execution_abandonments
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_abandonment_mutation();
        CREATE FUNCTION cloud.guard_execution_consumption_reservation() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE finalized_at timestamptz;
        BEGIN
            SELECT "FinalizedAt" INTO finalized_at FROM cloud.quota_reservations WHERE "Id" = NEW."ReservationId" FOR UPDATE;
            IF NOT FOUND OR finalized_at IS NOT NULL OR EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE "ReservationId" = NEW."ReservationId") THEN
                RAISE EXCEPTION 'reservation unavailable for consumption' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_z_guard_execution_consumption_reservation BEFORE INSERT ON cloud.execution_consumptions
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_execution_consumption_reservation();
        CREATE TRIGGER cloud_z_guard_execution_completion_reservation BEFORE INSERT ON cloud.execution_completions
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_execution_consumption_reservation();
        CREATE OR REPLACE FUNCTION cloud.guard_claimed_reservation_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW."FinalizedAt" IS NOT NULL AND OLD."FinalizedAt" IS NULL AND
               EXISTS (SELECT 1 FROM cloud.execution_claims WHERE "ReservationId" = OLD."Id") AND
               NOT (NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE "ReservationId" = OLD."Id")
                    AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions WHERE "ReservationId" = OLD."Id")) AND
               (NEW."Actual" <> OLD."Amount" OR NOT EXISTS (
                   SELECT 1 FROM cloud.execution_completions c JOIN cloud.execution_claims x
                     ON x."ReservationId" = c."ReservationId" AND x."ClaimId" = c."ClaimId"
                   WHERE c."ReservationId" = OLD."Id")) THEN
                RAISE EXCEPTION 'claimed execution requires matching completion or abandonment' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Abandonment and consumption fences must remain installed while recovery evidence exists.");
}
