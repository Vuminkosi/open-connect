using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionConsumption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execution_consumptions",
                schema: "cloud",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_consumptions", x => x.ReservationId);
                    table.ForeignKey(
                        name: "FK_execution_consumptions_execution_claims_ReservationId",
                        column: x => x.ReservationId,
                        principalSchema: "cloud",
                        principalTable: "execution_claims",
                        principalColumn: "ReservationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_execution_consumptions_worker_deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalSchema: "cloud",
                        principalTable: "worker_deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_consumptions_DeploymentId",
                schema: "cloud",
                table: "execution_consumptions",
                column: "DeploymentId");
            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.validate_execution_consumption() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM cloud.execution_claims c WHERE c."ReservationId" = NEW."ReservationId"
                        AND c."ClaimId" = NEW."ClaimId" AND c."DeploymentId" = NEW."DeploymentId" AND c."Region" IS NOT NULL) THEN
                        RAISE EXCEPTION 'consumption does not match a deployment-bound claim' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_validate_execution_consumption BEFORE INSERT ON cloud.execution_consumptions
                FOR EACH ROW EXECUTE FUNCTION cloud.validate_execution_consumption();
                CREATE FUNCTION cloud.reject_execution_consumption_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'cloud execution consumptions are append-only';
                END;
                $$;
                CREATE TRIGGER cloud_execution_consumption_append_only BEFORE UPDATE OR DELETE ON cloud.execution_consumptions
                FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_consumption_mutation();
                CREATE TRIGGER cloud_execution_consumption_no_truncate BEFORE TRUNCATE ON cloud.execution_consumptions
                FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_consumption_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Execution consumptions are append-only; roll back application code without dropping them.");
        }
    }
}
