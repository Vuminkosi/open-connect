using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121130000_AddLiveUIPrivacyEvidence")]
public sealed class AddLiveUIPrivacyEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.liveui_privacy_evidence (
            "Id" uuid PRIMARY KEY, "Sequence" bigint GENERATED ALWAYS AS IDENTITY,
            "TenantId" uuid NOT NULL, "ApplicationId" uuid NOT NULL, "RequestId" uuid NOT NULL,
            "Outcome" varchar(32) NOT NULL CHECK ("Outcome" IN ('allowed', 'privacy_blocked', 'detector_unavailable')),
            "TraceId" varchar(32) NOT NULL, "OccurredAt" timestamptz NOT NULL,
            "FingerprintKeyId" varchar(64) NOT NULL, "RequestFingerprint" bytea NOT NULL CHECK (octet_length("RequestFingerprint") = 32),
            FOREIGN KEY ("TenantId", "ApplicationId") REFERENCES cloud.applications ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX cloud_liveui_privacy_sequence ON cloud.liveui_privacy_evidence ("Sequence");
        CREATE INDEX cloud_liveui_privacy_request ON cloud.liveui_privacy_evidence ("TenantId", "ApplicationId", "RequestId", "Sequence" DESC);
        CREATE TRIGGER cloud_liveui_privacy_append_only BEFORE UPDATE OR DELETE ON cloud.liveui_privacy_evidence
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        CREATE TRIGGER cloud_liveui_privacy_no_truncate BEFORE TRUNCATE ON cloud.liveui_privacy_evidence
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("LiveUI privacy evidence must be retained during application rollback.");
}
