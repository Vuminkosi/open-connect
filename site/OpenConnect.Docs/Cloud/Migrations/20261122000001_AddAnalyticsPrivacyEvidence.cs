using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000001_AddAnalyticsPrivacyEvidence")]
public sealed class AddAnalyticsPrivacyEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.analytics_privacy_evidence (
            "Id" uuid PRIMARY KEY, "TenantId" uuid NOT NULL, "ApplicationId" uuid NOT NULL, "RequestId" uuid NOT NULL,
            "Outcome" varchar(32) NOT NULL CHECK ("Outcome" IN ('allowed', 'privacy_blocked', 'detector_unavailable')),
            "Direction" varchar(8) NOT NULL CHECK ("Direction" IN ('request', 'response')),
            "CategoryCounts" text NOT NULL,
            "TraceId" varchar(32) NOT NULL, "OccurredAt" timestamptz NOT NULL,
            "Sequence" bigint GENERATED ALWAYS AS IDENTITY, "FingerprintKeyId" varchar(64) NOT NULL,
            "RequestFingerprint" bytea NOT NULL CHECK (octet_length("RequestFingerprint") = 32),
            FOREIGN KEY ("TenantId", "ApplicationId") REFERENCES cloud.applications ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE UNIQUE INDEX cloud_analytics_privacy_sequence ON cloud.analytics_privacy_evidence ("Sequence");
        CREATE INDEX cloud_analytics_privacy_request ON cloud.analytics_privacy_evidence ("TenantId", "ApplicationId", "RequestId", "Direction", "Sequence" DESC);
        CREATE TRIGGER cloud_analytics_privacy_evidence_append_only BEFORE UPDATE OR DELETE ON cloud.analytics_privacy_evidence
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        CREATE TRIGGER cloud_analytics_privacy_evidence_no_truncate BEFORE TRUNCATE ON cloud.analytics_privacy_evidence
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Analytics privacy evidence and fingerprint bindings must be retained.");
}
