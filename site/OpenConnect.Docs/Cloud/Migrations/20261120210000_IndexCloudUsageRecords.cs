using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261120210000_IndexCloudUsageRecords")]
public sealed class IndexCloudUsageRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "CREATE INDEX \"IX_usage_records_TenantId_RecordedAt_ReservationId\" ON cloud.usage_records (\"TenantId\", \"RecordedAt\", \"ReservationId\");");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        "DROP INDEX cloud.\"IX_usage_records_TenantId_RecordedAt_ReservationId\";");
}
