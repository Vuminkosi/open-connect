using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261123000500_IndexVoicePrivacyObservations")]
public sealed class IndexVoicePrivacyObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE INDEX cloud_voice_privacy_tenant_timeline ON cloud.voice_privacy_evidence ("TenantId", "OccurredAt" DESC, "Id" DESC);
        CREATE INDEX cloud_voice_output_privacy_tenant_timeline ON cloud.voice_output_privacy_evidence ("TenantId", "OccurredAt" DESC, "Id" DESC);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP INDEX cloud.cloud_voice_output_privacy_tenant_timeline;
        DROP INDEX cloud.cloud_voice_privacy_tenant_timeline;
        """);
}
