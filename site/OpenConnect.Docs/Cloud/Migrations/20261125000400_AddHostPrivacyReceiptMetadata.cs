using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261125000400_AddHostPrivacyReceiptMetadata")]
public sealed class AddHostPrivacyReceiptMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE cloud.host_privacy_evidence ADD COLUMN "TraceId" character varying(128) NULL;
        ALTER TABLE cloud.host_privacy_evidence ADD COLUMN "RequestId" character varying(128) NULL;
        ALTER TABLE cloud.host_privacy_evidence ADD COLUMN "OccurredAt" timestamp with time zone NULL;
        ALTER TABLE cloud.host_privacy_evidence ADD COLUMN "PolicyId" character varying(128) NULL;
        ALTER TABLE cloud.host_privacy_evidence ADD COLUMN "PolicyRevision" character varying(64) NULL;
        ALTER TABLE cloud.host_privacy_evidence ADD COLUMN "IntegrityDigest" character varying(64) NULL;
        ALTER TABLE cloud.host_privacy_evidence ADD CONSTRAINT ck_host_privacy_integrity_digest CHECK
            ("IntegrityDigest" IS NULL OR "IntegrityDigest" ~ '^[0-9a-f]{64}$');
        CREATE INDEX ix_host_privacy_evidence_tenant_trace
            ON cloud.host_privacy_evidence ("TenantId", "TraceId")
            WHERE "TraceId" IS NOT NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Privacy evidence is append-only; roll back application code without dropping evidence.");
}
