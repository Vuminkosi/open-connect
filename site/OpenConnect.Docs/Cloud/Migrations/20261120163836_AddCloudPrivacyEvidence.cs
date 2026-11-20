using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudPrivacyEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "privacy_evidence",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TraceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_evidence", x => x.Id);
                    table.CheckConstraint("ck_privacy_evidence_outcome", "\"Outcome\" IN ('allowed', 'privacy_blocked', 'detector_unavailable')");
                    table.ForeignKey(
                        name: "FK_privacy_evidence_applications_TenantId_ApplicationId",
                        columns: x => new { x.TenantId, x.ApplicationId },
                        principalSchema: "cloud",
                        principalTable: "applications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_privacy_evidence_TenantId_ApplicationId_OccurredAt",
                schema: "cloud",
                table: "privacy_evidence",
                columns: new[] { "TenantId", "ApplicationId", "OccurredAt" });

            migrationBuilder.Sql("""
                CREATE FUNCTION cloud.reject_privacy_evidence_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'cloud privacy evidence is append-only';
                END;
                $$;
                CREATE TRIGGER cloud_privacy_evidence_append_only BEFORE UPDATE OR DELETE ON cloud.privacy_evidence
                FOR EACH ROW EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
                CREATE TRIGGER cloud_privacy_evidence_no_truncate BEFORE TRUNCATE ON cloud.privacy_evidence
                FOR EACH STATEMENT EXECUTE FUNCTION cloud.reject_privacy_evidence_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Privacy evidence is append-only; roll back application code without dropping the evidence table.");
        }
    }
}
