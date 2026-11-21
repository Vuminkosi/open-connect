using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261122000300_FenceLiveUIMeteredConsumption")]
public sealed class FenceLiveUIMeteredConsumption : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.quota_reservations, cloud.liveui_token_reservations, cloud.execution_claims, cloud.execution_consumptions IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE cloud.liveui_metered_consumption_authorizations (
            "PlanReservationId" uuid PRIMARY KEY REFERENCES cloud.liveui_token_reservations ("PlanReservationId") ON DELETE RESTRICT,
            "ClaimId" uuid NOT NULL,
            "DeploymentId" uuid NOT NULL,
            "ContextTokens" integer NOT NULL CHECK ("ContextTokens" > 0 AND "ContextTokens" <= 32768),
            "AuthorizedAt" timestamptz NOT NULL
        );
        CREATE FUNCTION cloud.validate_liveui_metered_authorization() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE plan_finalized timestamptz;
        DECLARE plan_expiry timestamptz;
        BEGIN
            SELECT "FinalizedAt", "ExpiresAt" INTO plan_finalized, plan_expiry FROM cloud.quota_reservations
                WHERE "Id" = NEW."PlanReservationId" FOR UPDATE;
            IF NOT FOUND OR plan_finalized IS NOT NULL OR plan_expiry <= clock_timestamp() OR
                EXISTS (SELECT 1 FROM cloud.execution_consumptions WHERE "ReservationId" = NEW."PlanReservationId") OR
                EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE "ReservationId" = NEW."PlanReservationId") OR
                NOT EXISTS (SELECT 1 FROM cloud.liveui_token_reservations t
                    JOIN cloud.execution_claims c ON c."ReservationId" = t."PlanReservationId" AND c."TenantId" = t."TenantId" AND c."ApplicationId" = t."ApplicationId"
                    WHERE t."PlanReservationId" = NEW."PlanReservationId" AND t."FinalizedAt" IS NULL AND t."Amount" >= NEW."ContextTokens"
                      AND c."ClaimId" = NEW."ClaimId" AND c."DeploymentId" = NEW."DeploymentId") THEN
                RAISE EXCEPTION 'LiveUI metered authorization is invalid' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER validate_liveui_metered_authorization BEFORE INSERT ON cloud.liveui_metered_consumption_authorizations
            FOR EACH ROW EXECUTE FUNCTION cloud.validate_liveui_metered_authorization();
        CREATE TRIGGER liveui_metered_authorization_append_only BEFORE UPDATE OR DELETE ON cloud.liveui_metered_consumption_authorizations
            FOR EACH ROW EXECUTE FUNCTION cloud.reject_execution_consumption_mutation();
        CREATE TRIGGER liveui_metered_authorization_no_truncate BEFORE TRUNCATE ON cloud.liveui_metered_consumption_authorizations
            FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_execution_consumption_mutation();
        CREATE FUNCTION cloud.guard_liveui_metered_consumption() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF EXISTS (SELECT 1 FROM cloud.liveui_token_reservations t WHERE t."PlanReservationId" = NEW."ReservationId") AND
                NOT EXISTS (SELECT 1 FROM cloud.liveui_metered_consumption_authorizations a
                    WHERE a."PlanReservationId" = NEW."ReservationId" AND a."ClaimId" = NEW."ClaimId" AND a."DeploymentId" = NEW."DeploymentId") THEN
                RAISE EXCEPTION 'LiveUI dual reservation requires metered worker authorization' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER guard_liveui_metered_consumption BEFORE INSERT ON cloud.execution_consumptions
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_liveui_metered_consumption();
        CREATE FUNCTION cloud.require_liveui_metered_consumption_at_commit() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM cloud.execution_consumptions c WHERE c."ReservationId" = NEW."PlanReservationId"
                AND c."ClaimId" = NEW."ClaimId" AND c."DeploymentId" = NEW."DeploymentId") THEN
                RAISE EXCEPTION 'LiveUI metered authorization must be consumed atomically' USING ERRCODE = '23514';
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE CONSTRAINT TRIGGER require_liveui_metered_consumption_at_commit
            AFTER INSERT ON cloud.liveui_metered_consumption_authorizations DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION cloud.require_liveui_metered_consumption_at_commit();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain the LiveUI metered-consumption fence during application rollback.");
}
