using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class IndexCloudPrivacyObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_privacy_evidence_TenantId_OccurredAt_Id",
                schema: "cloud",
                table: "privacy_evidence",
                columns: new[] { "TenantId", "OccurredAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_privacy_evidence_TenantId_OccurredAt_Id",
                schema: "cloud",
                table: "privacy_evidence");
        }
    }
}
