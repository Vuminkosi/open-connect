using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.Minimal.Migrations
{
    /// <inheritdoc />
    public partial class CurrentModelSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReceiptPayload",
                table: "oc_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptStatus",
                table: "oc_tasks",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandDigest",
                table: "oc_approvals",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandId",
                table: "oc_approvals",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsumedAtUtc",
                table: "oc_approvals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_approvals_TenantScope_PrincipalScope_TaskId_CommandId",
                table: "oc_approvals",
                columns: new[] { "TenantScope", "PrincipalScope", "TaskId", "CommandId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_oc_approvals_TenantScope_PrincipalScope_TaskId_CommandId",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "ReceiptPayload",
                table: "oc_tasks");

            migrationBuilder.DropColumn(
                name: "ReceiptStatus",
                table: "oc_tasks");

            migrationBuilder.DropColumn(
                name: "CommandDigest",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "CommandId",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "ConsumedAtUtc",
                table: "oc_approvals");
        }
    }
}
