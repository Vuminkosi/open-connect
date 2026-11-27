using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class InitialCloudControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cloud");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubjectIssuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.Id);
                    table.UniqueConstraint("AK_applications_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("ck_cloud_applications_environment", "\"Environment\" IN ('Development', 'Staging', 'Production')");
                    table.ForeignKey(
                        name: "FK_applications_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "cloud",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectIssuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memberships", x => new { x.TenantId, x.SubjectIssuer, x.SubjectId });
                    table.CheckConstraint("ck_cloud_memberships_role", "\"Role\" IN ('Owner', 'Administrator', 'SecurityAnalyst', 'Developer', 'BillingReader')");
                    table.ForeignKey(
                        name: "FK_memberships_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "cloud",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_applications_TenantId_Name",
                schema: "cloud",
                table: "applications",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_TenantId_OccurredAt",
                schema: "cloud",
                table: "audit_events",
                columns: new[] { "TenantId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_memberships_SubjectIssuer_SubjectId",
                schema: "cloud",
                table: "memberships",
                columns: new[] { "SubjectIssuer", "SubjectId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.reject_audit_event_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'cloud audit events are append-only';
                END;
                $$;
                CREATE TRIGGER cloud_audit_events_append_only
                BEFORE UPDATE OR DELETE ON cloud.audit_events
                FOR EACH ROW EXECUTE FUNCTION cloud.reject_audit_event_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "applications",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "cloud");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS cloud.reject_audit_event_mutation();");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "cloud");
        }
    }
}
