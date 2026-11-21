using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261123000300_GuardVoiceSettlement")]
public sealed class GuardVoiceSettlement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.execution_claims, cloud.execution_consumptions,
            cloud.execution_completions, cloud.execution_abandonments IN SHARE ROW EXCLUSIVE MODE;
        DO $voice_preflight$
        BEGIN
            IF EXISTS (SELECT 1 FROM cloud.execution_completions c
                JOIN cloud.quota_reservations r ON r."Id" = c."ReservationId"
                JOIN cloud.quota_buckets b ON b."Id" = r."BucketId"
                WHERE b."CapabilityId" = 'voice.synthesize' AND NOT EXISTS (
                    SELECT 1 FROM cloud.execution_consumptions u JOIN cloud.execution_claims x
                        ON x."ReservationId" = u."ReservationId" AND x."ClaimId" = u."ClaimId" AND x."DeploymentId" = u."DeploymentId"
                    WHERE u."ReservationId" = c."ReservationId" AND u."ClaimId" = c."ClaimId" AND c."Outcome" = 'completed')) THEN
                RAISE EXCEPTION 'existing voice completion lacks matching consumption';
            END IF;
            IF EXISTS (SELECT 1 FROM cloud.quota_reservations r JOIN cloud.quota_buckets b ON b."Id" = r."BucketId"
                JOIN cloud.execution_claims x ON x."ReservationId" = r."Id"
                WHERE b."CapabilityId" = 'voice.synthesize' AND r."FinalizedAt" IS NOT NULL AND (
                    (r."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments a WHERE a."ReservationId" = r."Id")
                        AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions u WHERE u."ReservationId" = r."Id")) OR
                    (r."Actual" = r."Amount" AND EXISTS (SELECT 1 FROM cloud.execution_consumptions u
                        JOIN cloud.execution_completions c ON c."ReservationId" = u."ReservationId" AND c."ClaimId" = u."ClaimId" AND c."Outcome" = 'completed'
                        WHERE u."ReservationId" = r."Id" AND u."ClaimId" = x."ClaimId" AND u."DeploymentId" = x."DeploymentId"))) IS NOT TRUE) THEN
                RAISE EXCEPTION 'existing voice settlement lacks matching evidence';
            END IF;
        END;
        $voice_preflight$;
        CREATE FUNCTION cloud.guard_voice_completion() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE is_voice boolean;
        DECLARE finalized timestamptz;
        BEGIN
            SELECT b."CapabilityId" = 'voice.synthesize', r."FinalizedAt" INTO is_voice, finalized
                FROM cloud.quota_reservations r JOIN cloud.quota_buckets b ON b."Id" = r."BucketId"
                WHERE r."Id" = NEW."ReservationId" FOR UPDATE OF r;
            IF NOT FOUND OR NOT is_voice THEN RETURN NEW; END IF;
            IF finalized IS NOT NULL OR NEW."Outcome" <> 'completed' OR NOT EXISTS (
                SELECT 1 FROM cloud.execution_consumptions u JOIN cloud.execution_claims x
                  ON x."ReservationId" = u."ReservationId" AND x."ClaimId" = u."ClaimId" AND x."DeploymentId" = u."DeploymentId"
                WHERE u."ReservationId" = NEW."ReservationId" AND u."ClaimId" = NEW."ClaimId") THEN
                RAISE EXCEPTION 'voice completion requires an unfinalized consumed claim' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_guard_voice_completion BEFORE INSERT ON cloud.execution_completions
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_voice_completion();
        CREATE FUNCTION cloud.guard_voice_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD."FinalizedAt" IS NOT NULL OR NEW."FinalizedAt" IS NULL OR NOT EXISTS (
                SELECT 1 FROM cloud.quota_buckets b WHERE b."Id" = OLD."BucketId" AND b."CapabilityId" = 'voice.synthesize') OR
                NOT EXISTS (SELECT 1 FROM cloud.execution_claims WHERE "ReservationId" = OLD."Id") THEN RETURN NEW; END IF;
            IF NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE "ReservationId" = OLD."Id")
                AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions WHERE "ReservationId" = OLD."Id") THEN RETURN NEW; END IF;
            IF NEW."Actual" IS DISTINCT FROM OLD."Amount" OR NOT EXISTS (
                SELECT 1 FROM cloud.execution_consumptions u
                JOIN cloud.execution_claims x ON x."ReservationId" = u."ReservationId" AND x."ClaimId" = u."ClaimId" AND x."DeploymentId" = u."DeploymentId"
                JOIN cloud.execution_completions c ON c."ReservationId" = u."ReservationId" AND c."ClaimId" = u."ClaimId" AND c."Outcome" = 'completed'
                WHERE u."ReservationId" = OLD."Id") THEN
                RAISE EXCEPTION 'voice finalization requires matching consumed completion' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_guard_voice_finalization BEFORE UPDATE ON cloud.quota_reservations
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_voice_finalization();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain speech settlement protection when rolling back application code.");
}
