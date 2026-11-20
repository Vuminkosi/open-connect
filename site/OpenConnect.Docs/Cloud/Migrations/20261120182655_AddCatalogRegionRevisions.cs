using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogRegionRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_region_revisions",
                schema: "cloud",
                columns: table => new
                {
                    CapabilityId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_region_revisions", x => x.CapabilityId);
                    table.CheckConstraint("ck_catalog_region_revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_catalog_region_revisions_capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "cloud",
                        principalTable: "capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
            migrationBuilder.Sql("""
                LOCK TABLE cloud.capabilities IN SHARE ROW EXCLUSIVE MODE;
                CREATE FUNCTION cloud.advance_catalog_region_revision() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        INSERT INTO cloud.catalog_region_revisions ("CapabilityId", "Revision") VALUES (NEW."Id", 1);
                    ELSIF NEW."AllowedRegions" IS DISTINCT FROM OLD."AllowedRegions" OR NEW."AllowedRegionsVerified" IS DISTINCT FROM OLD."AllowedRegionsVerified" THEN
                        INSERT INTO cloud.catalog_region_revisions ("CapabilityId", "Revision") VALUES (NEW."Id", 1)
                        ON CONFLICT ("CapabilityId") DO UPDATE SET "Revision" = cloud.catalog_region_revisions."Revision" + 1;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_advance_catalog_region_revision
                AFTER INSERT OR UPDATE OF "AllowedRegions", "AllowedRegionsVerified" ON cloud.capabilities
                FOR EACH ROW EXECUTE FUNCTION cloud.advance_catalog_region_revision();
                INSERT INTO cloud.catalog_region_revisions ("CapabilityId", "Revision")
                SELECT "Id", 1 FROM cloud.capabilities;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Preserve catalog region revisions when rolling back application code.");
        }
    }
}
