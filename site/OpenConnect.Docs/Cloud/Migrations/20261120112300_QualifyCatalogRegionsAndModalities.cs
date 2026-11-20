using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class QualifyCatalogRegionsAndModalities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowedRegionsVerified",
                schema: "cloud",
                table: "capabilities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ModalitiesVerified",
                schema: "cloud",
                table: "capabilities",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedRegionsVerified",
                schema: "cloud",
                table: "capabilities");

            migrationBuilder.DropColumn(
                name: "ModalitiesVerified",
                schema: "cloud",
                table: "capabilities");
        }
    }
}
