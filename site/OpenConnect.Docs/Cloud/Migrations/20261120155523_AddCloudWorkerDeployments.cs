using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudWorkerDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "worker_deployments",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EndpointReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Transport = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Region = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ModelRevision = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ModelDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: true),
                    ImageDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    ConcurrencyLimit = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_deployments", x => x.Id);
                    table.CheckConstraint("ck_worker_deployment_concurrency", "\"ConcurrencyLimit\" > 0 AND \"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_worker_deployments_capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "cloud",
                        principalTable: "capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_worker_deployments_CapabilityId_Region_Enabled",
                schema: "cloud",
                table: "worker_deployments",
                columns: new[] { "CapabilityId", "Region", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_worker_deployments_EndpointReference",
                schema: "cloud",
                table: "worker_deployments",
                column: "EndpointReference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worker_deployments",
                schema: "cloud");
        }
    }
}
