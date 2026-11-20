using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class BindQuotaRequestFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_quota_reservation_amounts",
                schema: "cloud",
                table: "quota_reservations");

            migrationBuilder.AddColumn<byte[]>(
                name: "RequestFingerprint",
                schema: "cloud",
                table: "quota_reservations",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_quota_reservation_amounts",
                schema: "cloud",
                table: "quota_reservations",
                sql: "\"Amount\" > 0 AND (\"Actual\" IS NULL OR (\"Actual\" >= 0 AND \"Actual\" <= \"Amount\")) AND ((\"Actual\" IS NULL) = (\"FinalizedAt\" IS NULL)) AND (\"RequestFingerprint\" IS NULL OR octet_length(\"RequestFingerprint\") = 32)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_quota_reservation_amounts",
                schema: "cloud",
                table: "quota_reservations");

            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                schema: "cloud",
                table: "quota_reservations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_quota_reservation_amounts",
                schema: "cloud",
                table: "quota_reservations",
                sql: "\"Amount\" > 0 AND (\"Actual\" IS NULL OR (\"Actual\" >= 0 AND \"Actual\" <= \"Amount\")) AND ((\"Actual\" IS NULL) = (\"FinalizedAt\" IS NULL))");
        }
    }
}
