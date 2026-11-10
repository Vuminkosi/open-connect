using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ModelSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceiptPayload",
                schema: "openconnect",
                table: "Tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptStatus",
                schema: "openconnect",
                table: "Tasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandDigest",
                schema: "openconnect",
                table: "Approvals",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandId",
                schema: "openconnect",
                table: "Approvals",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsumedAtUtc",
                schema: "openconnect",
                table: "Approvals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_TenantScope_PrincipalScope_TaskId_CommandId",
                schema: "openconnect",
                table: "Approvals",
                columns: new[] { "TenantScope", "PrincipalScope", "TaskId", "CommandId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Approvals_TenantScope_PrincipalScope_TaskId_CommandId",
                schema: "openconnect",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "ReceiptPayload",
                schema: "openconnect",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ReceiptStatus",
                schema: "openconnect",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "CommandDigest",
                schema: "openconnect",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "CommandId",
                schema: "openconnect",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "ConsumedAtUtc",
                schema: "openconnect",
                table: "Approvals");
        }
    }
}
