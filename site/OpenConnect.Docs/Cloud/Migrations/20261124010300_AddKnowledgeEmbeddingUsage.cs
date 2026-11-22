using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261124010300_AddKnowledgeEmbeddingUsage")]
public sealed class AddKnowledgeEmbeddingUsage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.knowledge_embedding_usage (
            "ReservationId" uuid PRIMARY KEY REFERENCES cloud.quota_reservations ("Id") ON DELETE RESTRICT,
            "TenantId" uuid NOT NULL, "ApplicationId" uuid NOT NULL, "ClaimId" uuid NOT NULL,
            "InputCharacters" integer NOT NULL CHECK ("InputCharacters" BETWEEN 1 AND 65536),
            "RecordedAt" timestamptz NOT NULL,
            FOREIGN KEY ("TenantId", "ApplicationId") REFERENCES cloud.applications ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE FUNCTION cloud.validate_knowledge_embedding_usage() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE reservation cloud.quota_reservations%ROWTYPE;
        DECLARE evidence cloud.knowledge_embedding_privacy_evidence%ROWTYPE;
        BEGIN
            PERFORM 1 FROM cloud.applications a WHERE a."TenantId" = NEW."TenantId" AND a."Id" = NEW."ApplicationId" FOR SHARE;
            IF NOT FOUND THEN RAISE EXCEPTION 'embedding application unavailable' USING ERRCODE = '23514'; END IF;
            SELECT r.* INTO reservation FROM cloud.quota_reservations r
                JOIN cloud.quota_buckets b ON b."Id" = r."BucketId" AND b."TenantId" = r."TenantId" AND b."ApplicationId" = r."ApplicationId"
                WHERE r."Id" = NEW."ReservationId" AND r."TenantId" = NEW."TenantId" AND r."ApplicationId" = NEW."ApplicationId"
                  AND r."FinalizedAt" IS NULL AND r."Amount" = 1 AND r."ExpiresAt" > clock_timestamp()
                  AND b."CapabilityId" = 'knowledge.embed' AND b."Unit" = 'request' FOR UPDATE OF r;
            IF NOT FOUND OR NOT EXISTS (
                SELECT 1 FROM cloud.execution_claims c
                JOIN cloud.execution_consumptions u ON u."ReservationId" = c."ReservationId" AND u."ClaimId" = c."ClaimId" AND u."DeploymentId" = c."DeploymentId"
                JOIN cloud.execution_completions x ON x."ReservationId" = c."ReservationId" AND x."ClaimId" = c."ClaimId" AND x."Outcome" = 'completed'
                WHERE c."ReservationId" = NEW."ReservationId" AND c."ClaimId" = NEW."ClaimId"
                  AND c."TenantId" = NEW."TenantId" AND c."ApplicationId" = NEW."ApplicationId") THEN
                RAISE EXCEPTION 'embedding usage requires a matching consumed completion' USING ERRCODE = '23514';
            END IF;
            SELECT * INTO evidence FROM cloud.knowledge_embedding_privacy_evidence e
                WHERE e."TenantId" = NEW."TenantId" AND e."ApplicationId" = NEW."ApplicationId"
                  AND e."RequestId" = reservation."RequestId" AND e."Direction" = 'request'
                ORDER BY e."Sequence" DESC LIMIT 1;
            IF NOT FOUND OR evidence."Outcome" <> 'allowed' OR evidence."InputCharacters" IS DISTINCT FROM NEW."InputCharacters"
                OR evidence."FingerprintKeyId" IS DISTINCT FROM reservation."FingerprintKeyId"
                OR evidence."RequestFingerprint" IS DISTINCT FROM reservation."RequestFingerprint" THEN
                RAISE EXCEPTION 'embedding usage requires matching request privacy evidence' USING ERRCODE = '23514';
            END IF;
            SELECT * INTO evidence FROM cloud.knowledge_embedding_privacy_evidence e
                WHERE e."TenantId" = NEW."TenantId" AND e."ApplicationId" = NEW."ApplicationId"
                  AND e."RequestId" = reservation."RequestId" AND e."Direction" = 'response'
                ORDER BY e."Sequence" DESC LIMIT 1;
            IF NOT FOUND OR evidence."Outcome" <> 'allowed'
                OR evidence."FingerprintKeyId" IS DISTINCT FROM reservation."FingerprintKeyId"
                OR evidence."RequestFingerprint" IS DISTINCT FROM reservation."RequestFingerprint" THEN
                RAISE EXCEPTION 'embedding usage requires matching response privacy evidence' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_validate_knowledge_embedding_usage BEFORE INSERT ON cloud.knowledge_embedding_usage
            FOR EACH ROW EXECUTE FUNCTION cloud.validate_knowledge_embedding_usage();
        CREATE TRIGGER cloud_knowledge_embedding_usage_append_only BEFORE UPDATE OR DELETE ON cloud.knowledge_embedding_usage
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        CREATE TRIGGER cloud_knowledge_embedding_usage_no_truncate BEFORE TRUNCATE ON cloud.knowledge_embedding_usage
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Knowledge embedding usage evidence must be retained.");
}
