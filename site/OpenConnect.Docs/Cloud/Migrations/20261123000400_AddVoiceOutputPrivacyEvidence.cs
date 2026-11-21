using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261123000400_AddVoiceOutputPrivacyEvidence")]
public sealed class AddVoiceOutputPrivacyEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.voice_output_privacy_evidence (
            "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "ApplicationId" uuid NOT NULL, "RequestId" uuid NOT NULL,
            "ReservationId" uuid NOT NULL, "ClaimId" uuid NOT NULL, "ModelFingerprint" varchar(64) NOT NULL,
            "Outcome" varchar(32) NOT NULL CHECK ("Outcome" IN ('allowed', 'privacy_blocked', 'detector_unavailable')),
            "TraceId" varchar(32) NOT NULL, "OccurredAt" timestamptz NOT NULL,
            FOREIGN KEY ("TenantId", "ApplicationId") REFERENCES cloud.applications ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE INDEX cloud_voice_output_privacy_scope ON cloud.voice_output_privacy_evidence ("TenantId", "ApplicationId", "OccurredAt");
        CREATE TRIGGER cloud_voice_output_privacy_append_only BEFORE UPDATE OR DELETE ON cloud.voice_output_privacy_evidence
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        CREATE TRIGGER cloud_voice_output_privacy_no_truncate BEFORE TRUNCATE ON cloud.voice_output_privacy_evidence
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Voice output privacy evidence must be retained during application rollback.");
}
