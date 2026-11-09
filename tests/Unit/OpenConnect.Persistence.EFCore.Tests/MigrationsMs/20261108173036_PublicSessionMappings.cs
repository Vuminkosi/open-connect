using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.EFCore.Tests.MigrationsMs
{
    /// <inheritdoc />
    public partial class PublicSessionMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicSessionMappings",
                schema: "openconnect",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "varchar(26)", unicode: false, maxLength: 26, nullable: false),
                    InternalSessionId = table.Column<string>(type: "varchar(26)", unicode: false, maxLength: 26, nullable: false),
                    PrincipalId = table.Column<string>(type: "varchar(26)", unicode: false, maxLength: 26, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicSessionMappings", x => new { x.TenantId, x.PublicId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicSessionMappings_InternalSessionId",
                schema: "openconnect",
                table: "PublicSessionMappings",
                column: "InternalSessionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicSessionMappings",
                schema: "openconnect");
        }
    }
}
