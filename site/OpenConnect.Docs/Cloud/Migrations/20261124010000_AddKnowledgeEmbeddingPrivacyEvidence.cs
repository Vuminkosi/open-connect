using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261124010000_AddKnowledgeEmbeddingPrivacyEvidence")]
public sealed class AddKnowledgeEmbeddingPrivacyEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.knowledge_embedding_privacy_evidence (
            "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "ApplicationId" uuid NOT NULL, "RequestId" uuid NOT NULL,
            "Outcome" varchar(32) NOT NULL CHECK ("Outcome" IN ('allowed', 'privacy_blocked', 'detector_unavailable')),
            "Direction" varchar(8) NOT NULL CHECK ("Direction" IN ('request', 'response')),
            "CategoryCounts" text NOT NULL,
            "TraceId" varchar(32) NOT NULL, "OccurredAt" timestamptz NOT NULL,
            "Sequence" bigint GENERATED ALWAYS AS IDENTITY, "FingerprintKeyId" varchar(64) NOT NULL,
            "RequestFingerprint" bytea NOT NULL CHECK (octet_length("RequestFingerprint") = 32),
            "InputCharacters" integer CHECK (("Direction" = 'request' AND "InputCharacters" IS NOT NULL AND "InputCharacters" BETWEEN 1 AND 65536) OR
                ("Direction" = 'response' AND "InputCharacters" IS NULL)),
            FOREIGN KEY ("TenantId", "ApplicationId") REFERENCES cloud.applications ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX cloud_knowledge_embedding_privacy_sequence ON cloud.knowledge_embedding_privacy_evidence ("Sequence");
        CREATE INDEX cloud_knowledge_embedding_privacy_request ON cloud.knowledge_embedding_privacy_evidence ("TenantId", "ApplicationId", "RequestId", "Direction", "Sequence" DESC);
        CREATE TRIGGER cloud_knowledge_embedding_privacy_evidence_append_only BEFORE UPDATE OR DELETE ON cloud.knowledge_embedding_privacy_evidence
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        CREATE TRIGGER cloud_knowledge_embedding_privacy_evidence_no_truncate BEFORE TRUNCATE ON cloud.knowledge_embedding_privacy_evidence
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Knowledge embedding privacy evidence and fingerprint bindings must be retained.");
}
