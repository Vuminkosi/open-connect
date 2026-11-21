using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000100_AddLiveUITokenReservations")]
public sealed class AddLiveUITokenReservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE cloud.liveui_token_reservations (
            "PlanReservationId" uuid PRIMARY KEY,
            "TenantId" uuid NOT NULL,
            "ApplicationId" uuid NOT NULL,
            "TokenBucketId" uuid NOT NULL,
            "Amount" bigint NOT NULL CHECK ("Amount" > 0 AND "Amount" <= 32768),
            "Actual" bigint NULL,
            "FinalizedAt" timestamptz NULL,
            CONSTRAINT ck_liveui_token_finalization CHECK (
                ("Actual" IS NULL AND "FinalizedAt" IS NULL) OR
                ("Actual" IS NOT NULL AND "FinalizedAt" IS NOT NULL AND "Actual" >= 0 AND "Actual" <= "Amount")),
            FOREIGN KEY ("TenantId", "ApplicationId", "PlanReservationId")
                REFERENCES cloud.quota_reservations ("TenantId", "ApplicationId", "Id") ON DELETE RESTRICT,
            FOREIGN KEY ("TenantId", "ApplicationId", "TokenBucketId")
                REFERENCES cloud.quota_buckets ("TenantId", "ApplicationId", "Id") ON DELETE RESTRICT
        );
        CREATE INDEX ix_liveui_token_reservations_bucket ON cloud.liveui_token_reservations ("TenantId", "ApplicationId", "TokenBucketId");
        CREATE FUNCTION cloud.guard_liveui_token_reservation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'INSERT' AND (NEW."Actual" IS NOT NULL OR NEW."FinalizedAt" IS NOT NULL) THEN
                RAISE EXCEPTION 'LiveUI token reservation must begin unfinalized' USING ERRCODE = '23514';
            END IF;
            IF TG_OP = 'UPDATE' AND (NEW."PlanReservationId", NEW."TenantId", NEW."ApplicationId", NEW."TokenBucketId", NEW."Amount")
                IS DISTINCT FROM (OLD."PlanReservationId", OLD."TenantId", OLD."ApplicationId", OLD."TokenBucketId", OLD."Amount") THEN
                RAISE EXCEPTION 'LiveUI token reservation identity is immutable' USING ERRCODE = '23514';
            END IF;
            IF TG_OP = 'UPDATE' AND OLD."FinalizedAt" IS NOT NULL AND NEW IS DISTINCT FROM OLD THEN
                RAISE EXCEPTION 'Finalized LiveUI token reservation is immutable' USING ERRCODE = '23514';
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM cloud.quota_reservations r
                JOIN cloud.quota_buckets plan ON plan."Id" = r."BucketId" AND plan."TenantId" = r."TenantId" AND plan."ApplicationId" = r."ApplicationId"
                JOIN cloud.quota_buckets tokens ON tokens."Id" = NEW."TokenBucketId" AND tokens."TenantId" = r."TenantId" AND tokens."ApplicationId" = r."ApplicationId"
                WHERE r."Id" = NEW."PlanReservationId" AND r."TenantId" = NEW."TenantId" AND r."ApplicationId" = NEW."ApplicationId"
                  AND plan."CapabilityId" = 'inference.liveui' AND plan."Unit" = 'plan' AND r."Amount" = 1
                  AND (TG_OP <> 'INSERT' OR r."FinalizedAt" IS NULL)
                  AND tokens."CapabilityId" = 'inference.liveui' AND tokens."Unit" = 'token') THEN
                RAISE EXCEPTION 'LiveUI token reservation scope is invalid' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER guard_liveui_token_reservation BEFORE INSERT OR UPDATE ON cloud.liveui_token_reservations
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_liveui_token_reservation();
        CREATE FUNCTION cloud.reject_liveui_token_reservation_removal() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'LiveUI token reservations must be retained' USING ERRCODE = '23514';
        END;
        $$;
        CREATE TRIGGER reject_liveui_token_reservation_delete BEFORE DELETE ON cloud.liveui_token_reservations
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_liveui_token_reservation_removal();
        CREATE TRIGGER reject_liveui_token_reservation_truncate BEFORE TRUNCATE ON cloud.liveui_token_reservations
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_liveui_token_reservation_removal();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("LiveUI token reservations must be retained during application rollback.");
}
