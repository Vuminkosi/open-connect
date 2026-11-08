using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.EFCore.Tests.MigrationsPg
{
    /// <inheritdoc />
    public partial class ApprovalNonces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalNonces",
                schema: "openconnect",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(26)", unicode: false, maxLength: 26, nullable: false),
                    Nonce = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(26)", unicode: false, maxLength: 26, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalNonces", x => new { x.TenantId, x.Nonce });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalNonces_ApprovalId",
                schema: "openconnect",
                table: "ApprovalNonces",
                column: "ApprovalId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalNonces",
                schema: "openconnect");
        }
    }
}
