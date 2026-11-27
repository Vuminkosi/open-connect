using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddClaimedClassificationCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "execution_completions",
                schema: "cloud",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_execution_completions", x => x.ReservationId);
                    table.CheckConstraint("ck_execution_completion_outcome", "\"Outcome\" = 'completed'");
                    table.ForeignKey(
                        name: "FK_execution_completions_execution_claims_ReservationId",
                        column: x => x.ReservationId,
                        principalSchema: "cloud",
                        principalTable: "execution_claims",
                        principalColumn: "ReservationId",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.validate_execution_completion() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM cloud.execution_claims c WHERE c."ReservationId" = NEW."ReservationId" AND c."ClaimId" = NEW."ClaimId") THEN
                        RAISE EXCEPTION 'completion claim does not match reservation' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_validate_execution_completion BEFORE INSERT ON cloud.execution_completions
                FOR EACH ROW EXECUTE FUNCTION cloud.validate_execution_completion();
                CREATE FUNCTION cloud.reject_execution_completion_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'cloud execution completions are append-only';
                END;
                $$;
                CREATE TRIGGER cloud_execution_completion_append_only BEFORE UPDATE OR DELETE ON cloud.execution_completions
                FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_completion_mutation();
                CREATE TRIGGER cloud_execution_completion_no_truncate BEFORE TRUNCATE ON cloud.execution_completions
                FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_completion_mutation();

                CREATE OR REPLACE FUNCTION cloud.guard_claimed_reservation_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."FinalizedAt" IS NOT NULL AND OLD."FinalizedAt" IS NULL AND
                       EXISTS (SELECT 1 FROM cloud.execution_claims WHERE "ReservationId" = OLD."Id") AND
                       (NEW."Actual" <> OLD."Amount" OR NOT EXISTS (
                           SELECT 1 FROM cloud.execution_completions c
                           JOIN cloud.execution_claims x ON x."ReservationId" = c."ReservationId" AND x."ClaimId" = c."ClaimId"
                           WHERE c."ReservationId" = OLD."Id")) THEN
                        RAISE EXCEPTION 'claimed execution requires matching full-charge completion' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Execution completions are append-only; roll back application code without dropping them.");
        }
    }
}
