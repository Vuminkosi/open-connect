using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotaLimitRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Revision",
                schema: "cloud",
                table: "quota_buckets",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.advance_quota_limit_revision() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Revision" <> OLD."Revision" THEN
                        RAISE EXCEPTION 'quota revision is managed by the database';
                    END IF;
                    IF NEW."Limit" IS DISTINCT FROM OLD."Limit" THEN
                        NEW."Revision" := OLD."Revision" + 1;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER advance_quota_limit_revision
                BEFORE UPDATE OF "Limit", "Revision" ON cloud.quota_buckets
                FOR EACH ROW EXECUTE FUNCTION cloud.advance_quota_limit_revision();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER advance_quota_limit_revision ON cloud.quota_buckets; DROP FUNCTION cloud.advance_quota_limit_revision();");
            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "cloud",
                table: "quota_buckets");
        }
    }
}
