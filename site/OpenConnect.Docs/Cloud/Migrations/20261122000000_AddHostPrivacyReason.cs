using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000000_AddHostPrivacyReason")]
public sealed class AddHostPrivacyReason : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE cloud.host_privacy_evidence ADD COLUMN "ReasonCode" character varying(32) NOT NULL DEFAULT 'not_recorded';
        ALTER TABLE cloud.host_privacy_evidence ADD CONSTRAINT ck_host_privacy_reason CHECK
            ("ReasonCode" IN ('allowed', 'inspection_failed', 'enforcement_unavailable', 'sensitive_detected', 'policy_blocked', 'not_recorded'));
        ALTER TABLE cloud.host_privacy_evidence ADD CONSTRAINT ck_host_privacy_reason_decision CHECK
            ("ReasonCode" = 'not_recorded' OR ("ReasonCode" = 'allowed') = "Allowed");
        ALTER TABLE cloud.host_privacy_evidence ADD CONSTRAINT ck_host_privacy_reason_evidence CHECK
            ("ReasonCode" <> 'sensitive_detected' OR "CategoryCounts" <> '{}'::jsonb);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Privacy evidence is append-only; roll back application code without dropping evidence.");
}
