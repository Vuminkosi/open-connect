using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudExecutionClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execution_claims",
                schema: "cloud",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_claims", x => x.ReservationId);
                    table.ForeignKey(
                        name: "FK_execution_claims_quota_reservations_TenantId_ApplicationId_~",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ReservationId },
                        principalSchema: "cloud",
                        principalTable: "quota_reservations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_execution_claims_ClaimId",
                schema: "cloud",
                table: "execution_claims",
                column: "ClaimId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_execution_claims_TenantId_ApplicationId_ReservationId",
                schema: "cloud",
                table: "execution_claims",
                columns: new[] { "TenantId", "ApplicationId", "ReservationId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.reject_execution_claim_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'cloud execution claims are append-only';
                END;
                $$;
                CREATE TRIGGER cloud_execution_claim_append_only BEFORE UPDATE OR DELETE ON cloud.execution_claims
                FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
                CREATE TRIGGER cloud_execution_claim_no_truncate BEFORE TRUNCATE ON cloud.execution_claims
                FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_claim_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Execution claims are append-only; roll back application code without dropping claims.");
        }
    }
}
