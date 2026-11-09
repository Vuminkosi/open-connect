using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.EFCore.Tests.MigrationsPg
{
    /// <inheritdoc />
    public partial class ApprovalNonceRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                schema: "openconnect",
                table: "ApprovalNonces",
                type: "character varying(256)",
                unicode: false,
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalNonces_RequestId",
                schema: "openconnect",
                table: "ApprovalNonces",
                column: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalNonces_RequestId",
                schema: "openconnect",
                table: "ApprovalNonces");

            migrationBuilder.DropColumn(
                name: "RequestId",
                schema: "openconnect",
                table: "ApprovalNonces");
        }
    }
}
