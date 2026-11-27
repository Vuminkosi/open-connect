using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class GuardDeploymentConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.guard_deployment_concurrency() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE maximum integer;
                DECLARE active_count bigint;
                BEGIN
                    IF NEW."DeploymentId" IS NULL THEN RETURN NEW; END IF;
                    -- A row version change also makes stale repeatable-read writers abort rather than count from an old snapshot.
                    UPDATE cloud.worker_deployments SET "ConcurrencyLimit" = "ConcurrencyLimit"
                        WHERE "Id" = NEW."DeploymentId" AND "Enabled" AND "CapabilityId" = 'classification.classify'
                        RETURNING "ConcurrencyLimit" INTO maximum;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'deployment unavailable' USING ERRCODE = '23514';
                    END IF;
                    SELECT count(*) INTO active_count FROM cloud.execution_claims c
                        JOIN cloud.quota_reservations r ON r."Id" = c."ReservationId"
                        WHERE c."DeploymentId" = NEW."DeploymentId" AND r."FinalizedAt" IS NULL;
                    IF active_count >= maximum THEN
                        RAISE EXCEPTION 'deployment capacity unavailable' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_guard_deployment_concurrency BEFORE INSERT ON cloud.execution_claims
                FOR EACH ROW EXECUTE FUNCTION cloud.guard_deployment_concurrency();
                """);
        }
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Deployment concurrency protection must remain installed while claims may execute.");
        }
    }
}
