using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenConnect.Docs.Cloud.Migrations;

[DbContext(typeof(CloudControlPlaneDbContext))]
[Migration("20261123000600_AddCapabilityAvailabilityRevisions")]
public sealed class AddCapabilityAvailabilityRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        LOCK TABLE cloud.capabilities IN SHARE ROW EXCLUSIVE MODE;
        CREATE TABLE cloud.capability_availability_revisions (
            "CapabilityId" varchar(80) PRIMARY KEY REFERENCES cloud.capabilities ("Id") ON DELETE RESTRICT,
            "Revision" bigint NOT NULL CHECK ("Revision" > 0)
        );
        CREATE FUNCTION cloud.advance_capability_availability_revision() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'INSERT' THEN
                INSERT INTO cloud.capability_availability_revisions ("CapabilityId", "Revision") VALUES (NEW."Id", 1);
            ELSIF NEW."PlatformAvailability" IS DISTINCT FROM OLD."PlatformAvailability" THEN
                INSERT INTO cloud.capability_availability_revisions ("CapabilityId", "Revision") VALUES (NEW."Id", 1)
                ON CONFLICT ("CapabilityId") DO UPDATE SET "Revision" = cloud.capability_availability_revisions."Revision" + 1;
            END IF;
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER cloud_advance_capability_availability_revision
        AFTER INSERT OR UPDATE OF "PlatformAvailability" ON cloud.capabilities
        FOR EACH ROW EXECUTE FUNCTION cloud.advance_capability_availability_revision();
        INSERT INTO cloud.capability_availability_revisions ("CapabilityId", "Revision")
        SELECT "Id", 1 FROM cloud.capabilities;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Preserve capability availability revisions when rolling back application code.");
}
