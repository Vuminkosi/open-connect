using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261121000000_GuardEntitlementRevision")]
public sealed class GuardEntitlementRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION cloud.guard_entitlement_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'revoke entitlements instead of deleting them' USING ERRCODE = '23514'; END IF;
            IF OLD."Revision" = 2147483647 THEN RAISE EXCEPTION 'entitlement revision exhausted' USING ERRCODE = '23514'; END IF;
            NEW."Revision" := OLD."Revision" + 1;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_guard_entitlement_revision BEFORE UPDATE OR DELETE ON cloud.tenant_entitlements
            FOR EACH ROW EXECUTE FUNCTION cloud.guard_entitlement_revision();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain entitlement revision fencing when rolling back application code.");
}
