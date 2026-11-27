using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class GuardClaimReservationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.guard_claim_reservation_state() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE finalized_at timestamptz;
                BEGIN
                    SELECT "FinalizedAt" INTO finalized_at FROM cloud.quota_reservations
                        WHERE "Id" = NEW."ReservationId" AND "TenantId" = NEW."TenantId" AND "ApplicationId" = NEW."ApplicationId"
                        FOR UPDATE;
                    IF NOT FOUND OR finalized_at IS NOT NULL THEN
                        RAISE EXCEPTION 'reservation is unavailable for claim' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_z_guard_claim_reservation_state BEFORE INSERT ON cloud.execution_claims
                FOR EACH ROW EXECUTE FUNCTION cloud.guard_claim_reservation_state();
                """);
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Claim reservation fencing must remain installed while recovery is enabled.");
        }
    }
}
