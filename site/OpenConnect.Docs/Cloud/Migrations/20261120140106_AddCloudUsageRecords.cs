using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    public partial class AddCloudUsageRecords : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_quota_reservations_TenantId_ApplicationId_Id",
                schema: "cloud",
                table: "quota_reservations",
                columns: new[] { "TenantId", "ApplicationId", "Id" });

            migrationBuilder.CreateTable(
                name: "usage_records",
                schema: "cloud",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Provenance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Unit = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_records", x => x.ReservationId);
                    table.CheckConstraint("ck_usage_quantity", "\"Quantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_usage_records_quota_reservations_TenantId_ApplicationId_Res~",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ReservationId },
                        principalSchema: "cloud",
                        principalTable: "quota_reservations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usage_records_TenantId_ApplicationId_RecordedAt",
                schema: "cloud",
                table: "usage_records",
                columns: new[] { "TenantId", "ApplicationId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_usage_records_TenantId_ApplicationId_ReservationId",
                schema: "cloud",
                table: "usage_records",
                columns: new[] { "TenantId", "ApplicationId", "ReservationId" });

            migrationBuilder.Sql("""
                INSERT INTO cloud.usage_records ("ReservationId", "TenantId", "ApplicationId", "RequestId", "Quantity", "RecordedAt", "Provenance")
                SELECT "Id", "TenantId", "ApplicationId", "RequestId", "Actual", "FinalizedAt", 'legacy_reservation'
                FROM cloud.quota_reservations WHERE "FinalizedAt" IS NOT NULL;

                CREATE FUNCTION cloud.capture_reservation_usage() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."FinalizedAt" IS NOT NULL AND (TG_OP = 'INSERT' OR OLD."FinalizedAt" IS NULL) THEN
                        INSERT INTO cloud.usage_records ("ReservationId", "TenantId", "ApplicationId", "RequestId", "Quantity", "RecordedAt", "Provenance", "CapabilityId", "Unit")
                        SELECT NEW."Id", NEW."TenantId", NEW."ApplicationId", NEW."RequestId", NEW."Actual", NEW."FinalizedAt", 'reservation', b."CapabilityId", b."Unit"
                        FROM cloud.quota_buckets b WHERE b."Id" = NEW."BucketId" AND b."TenantId" = NEW."TenantId" AND b."ApplicationId" = NEW."ApplicationId";
                        IF NOT FOUND THEN RAISE EXCEPTION 'quota bucket unavailable for usage'; END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_capture_reservation_usage AFTER INSERT OR UPDATE ON cloud.quota_reservations
                FOR EACH ROW EXECUTE FUNCTION cloud.capture_reservation_usage();

                CREATE FUNCTION cloud.protect_finalized() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."FinalizedAt" IS NOT NULL THEN RAISE EXCEPTION 'finalized quota reservations are immutable'; END IF;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER cloud_protect_finalized_reservation BEFORE UPDATE OR DELETE ON cloud.quota_reservations
                FOR EACH ROW EXECUTE FUNCTION cloud.protect_finalized();

                CREATE FUNCTION cloud.reject_usage_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'cloud usage records are append-only';
                END;
                $$;
                CREATE TRIGGER cloud_usage_append_only BEFORE UPDATE OR DELETE ON cloud.usage_records
                FOR EACH ROW EXECUTE FUNCTION cloud.reject_usage_mutation();
                CREATE TRIGGER cloud_usage_no_truncate BEFORE TRUNCATE ON cloud.usage_records
                FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_usage_mutation();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Usage records are append-only; roll back application code without dropping the usage ledger.");
        }
    }
}
