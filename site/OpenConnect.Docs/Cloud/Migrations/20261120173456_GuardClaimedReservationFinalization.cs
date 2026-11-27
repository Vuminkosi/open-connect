using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class GuardClaimedReservationFinalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE cloud.quota_reservations, cloud.execution_claims IN SHARE ROW EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM cloud.execution_claims c JOIN cloud.quota_reservations r ON r."Id" = c."ReservationId" WHERE r."FinalizedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'claimed reservations were already finalized; reconcile before installing the guard';
                    END IF;
                END;
                $$;
                CREATE FUNCTION cloud.guard_claimed_reservation_finalization() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."FinalizedAt" IS NOT NULL AND OLD."FinalizedAt" IS NULL AND
                       EXISTS (SELECT 1 FROM cloud.execution_claims WHERE "ReservationId" = OLD."Id") THEN
                        RAISE EXCEPTION 'claimed execution requires reconciliation' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_guard_claimed_reservation_finalization
                BEFORE UPDATE OF "Actual", "FinalizedAt" ON cloud.quota_reservations
                FOR EACH ROW EXECUTE FUNCTION cloud.guard_claimed_reservation_finalization();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("The claimed-reservation guard must remain installed while execution claims exist.");
        }
    }
}
