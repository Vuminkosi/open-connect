using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121011000_BindInferencePrivacyEvidence")]
public sealed class BindInferencePrivacyEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE cloud.inference_privacy_evidence ADD COLUMN "Sequence" bigint GENERATED ALWAYS AS IDENTITY;
        ALTER TABLE cloud.inference_privacy_evidence ADD COLUMN "FingerprintKeyId" varchar(64);
        ALTER TABLE cloud.inference_privacy_evidence ADD COLUMN "RequestFingerprint" bytea;
        ALTER TABLE cloud.inference_privacy_evidence ADD CONSTRAINT ck_inference_privacy_fingerprint CHECK (
            ("FingerprintKeyId" IS NULL AND "RequestFingerprint" IS NULL) OR
            ("FingerprintKeyId" IS NOT NULL AND "RequestFingerprint" IS NOT NULL AND octet_length("RequestFingerprint") = 32));
        CREATE UNIQUE INDEX cloud_inference_privacy_sequence ON cloud.inference_privacy_evidence ("Sequence");
        CREATE INDEX cloud_inference_privacy_request ON cloud.inference_privacy_evidence ("TenantId", "ApplicationId", "RequestId", "Sequence" DESC);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Inference privacy evidence and fingerprint bindings must be retained.");
}
