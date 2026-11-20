using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121131000_AllowLiveUIDeploymentClaims")]
public sealed class AllowLiveUIDeploymentClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.execution_claims, cloud.worker_deployments IN SHARE ROW EXCLUSIVE MODE;
        CREATE OR REPLACE FUNCTION cloud.guard_deployment_concurrency() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE maximum integer;
        DECLARE active_count bigint;
        BEGIN
            IF NEW."DeploymentId" IS NULL THEN RETURN NEW; END IF;
            UPDATE cloud.worker_deployments d SET "ConcurrencyLimit" = d."ConcurrencyLimit"
                WHERE d."Id" = NEW."DeploymentId" AND d."Enabled" AND d."CapabilityId" IN ('classification.classify', 'inference.chat', 'inference.liveui')
                  AND EXISTS (SELECT 1 FROM cloud.quota_reservations r JOIN cloud.quota_buckets b
                    ON b."Id" = r."BucketId" AND b."TenantId" = r."TenantId" AND b."ApplicationId" = r."ApplicationId"
                    WHERE r."Id" = NEW."ReservationId" AND r."TenantId" = NEW."TenantId" AND r."ApplicationId" = NEW."ApplicationId"
                      AND b."CapabilityId" = d."CapabilityId")
                RETURNING d."ConcurrencyLimit" INTO maximum;
            IF NOT FOUND THEN RAISE EXCEPTION 'deployment unavailable' USING ERRCODE = '23514'; END IF;
            SELECT count(*) INTO active_count FROM cloud.execution_claims c
                JOIN cloud.quota_reservations r ON r."Id" = c."ReservationId"
                WHERE c."DeploymentId" = NEW."DeploymentId" AND r."FinalizedAt" IS NULL;
            IF active_count >= maximum THEN RAISE EXCEPTION 'deployment capacity unavailable' USING ERRCODE = '23514'; END IF;
            RETURN NEW;
        END;
        $$;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain deployment concurrency protection when rolling back application code.");
}
