using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotaFingerprintKeyVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FingerprintKeyId",
                schema: "cloud",
                table: "quota_reservations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FingerprintKeyId",
                schema: "cloud",
                table: "quota_reservations");
        }
    }
}
