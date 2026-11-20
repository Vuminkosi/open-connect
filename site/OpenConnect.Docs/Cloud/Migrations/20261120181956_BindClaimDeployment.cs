using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class BindClaimDeployment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeploymentId",
                schema: "cloud",
                table: "execution_claims",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                schema: "cloud",
                table: "execution_claims",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_execution_claim_deployment",
                schema: "cloud",
                table: "execution_claims",
                sql: "(\"DeploymentId\" IS NULL) = (\"Region\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_execution_claims_DeploymentId",
                schema: "cloud",
                table: "execution_claims",
                column: "DeploymentId");

            migrationBuilder.AddForeignKey(
                name: "FK_execution_claims_worker_deployments_DeploymentId",
                schema: "cloud",
                table: "execution_claims",
                column: "DeploymentId",
                principalSchema: "cloud",
                principalTable: "worker_deployments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Preserve deployment and region evidence when rolling back application code.");
        }
    }
}
