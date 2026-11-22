using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121235959_AddHostPrivacyEvidence")]
public sealed class AddHostPrivacyEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.host_privacy_evidence (
            "EventId" uuid PRIMARY KEY,
            "TenantId" uuid NOT NULL,
            "ApplicationId" uuid NOT NULL,
            "HostTenantId" character varying(26) NOT NULL,
            "Destination" character varying(32) NOT NULL,
            "Allowed" boolean NOT NULL,
            "Action" character varying(16) NOT NULL,
            "CategoryCounts" jsonb NOT NULL,
            "ReceivedAt" timestamp with time zone NOT NULL,
            CHECK ("EventId" <> '00000000-0000-0000-0000-000000000000'::uuid),
            CHECK ("HostTenantId" ~ '^[0-7][0-9A-HJKMNP-TV-Z]{25}$' AND "HostTenantId" <> '00000000000000000000000000'),
            CHECK ("Destination" IN ('ExternalModel', 'Tool', 'Worker', 'Output', 'Nats', 'WebSocket', 'AuditLog')),
            CHECK ("Action" IN ('Allow', 'Mask', 'Redact', 'Block', 'Review')),
            CHECK (jsonb_typeof("CategoryCounts") = 'object'),
            FOREIGN KEY ("TenantId", "ApplicationId") REFERENCES cloud.applications ("TenantId", "Id") ON DELETE RESTRICT
        );
        CREATE INDEX ix_host_privacy_evidence_tenant_time ON cloud.host_privacy_evidence ("TenantId", "ReceivedAt", "EventId");
        CREATE TRIGGER cloud_host_privacy_evidence_append_only BEFORE UPDATE OR DELETE ON cloud.host_privacy_evidence
        FOR EACH ROW EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        CREATE TRIGGER cloud_host_privacy_evidence_no_truncate BEFORE TRUNCATE ON cloud.host_privacy_evidence
        FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Privacy evidence is append-only; roll back application code without dropping evidence.");
}
