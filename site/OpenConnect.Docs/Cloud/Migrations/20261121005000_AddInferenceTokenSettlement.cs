using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121005000_AddInferenceTokenSettlement")]
public sealed class AddInferenceTokenSettlement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.execution_claims, cloud.execution_consumptions, cloud.execution_completions IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE cloud.inference_token_observations (
            "ReservationId" uuid PRIMARY KEY REFERENCES cloud.execution_consumptions ("ReservationId") ON DELETE RESTRICT,
            "ClaimId" uuid NOT NULL,
            "InputTokens" bigint NOT NULL CHECK ("InputTokens" >= 0),
            "OutputTokens" bigint NOT NULL CHECK ("OutputTokens" >= 0),
            CHECK ("InputTokens"::numeric + "OutputTokens"::numeric <= 9223372036854775807)
        );
        CREATE TRIGGER cloud_inference_token_append_only BEFORE UPDATE OR DELETE ON cloud.inference_token_observations
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_completion_mutation();
        CREATE TRIGGER cloud_inference_token_no_truncate BEFORE TRUNCATE ON cloud.inference_token_observations
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_completion_mutation();
        CREATE OR REPLACE FUNCTION cloud.guard_claimed_reservation_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE inference_bucket boolean;
        BEGIN
            IF NEW."FinalizedAt" IS NULL OR OLD."FinalizedAt" IS NOT NULL OR
               NOT EXISTS (SELECT 1 FROM cloud.execution_claims WHERE "ReservationId" = OLD."Id") THEN RETURN NEW; END IF;
            IF NEW."Actual" = 0 AND EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE "ReservationId" = OLD."Id")
                AND NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions WHERE "ReservationId" = OLD."Id") THEN RETURN NEW; END IF;
            SELECT EXISTS (SELECT 1 FROM cloud.quota_buckets b WHERE b."Id" = OLD."BucketId" AND b."CapabilityId" = 'inference.chat') INTO inference_bucket;
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
        throw new NotSupportedException("Retain inference token evidence and settlement fences when rolling back application code.");
}
