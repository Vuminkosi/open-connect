using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261120230000_AddPrivacyEvidenceCategories")]
public sealed class AddPrivacyEvidenceCategories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "CategoryCounts", schema: "cloud", table: "privacy_evidence", type: "jsonb", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Privacy evidence is append-only; roll back application code without dropping evidence.");
}
