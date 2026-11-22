using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudQuotaReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quota_buckets",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Unit = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Limit = table.Column<long>(type: "bigint", nullable: false),
                    Consumed = table.Column<long>(type: "bigint", nullable: false),
                    Reserved = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_buckets", x => x.Id);
                    table.CheckConstraint("ck_quota_bucket_amounts", "\"Limit\" >= 0 AND \"Consumed\" >= 0 AND \"Reserved\" >= 0 AND \"Consumed\" <= \"Limit\" - \"Reserved\"");
                    table.CheckConstraint("ck_quota_bucket_period", "\"PeriodStart\" < \"PeriodEnd\"");
                    table.ForeignKey(
                        name: "FK_quota_buckets_applications_TenantId_ApplicationId",
                        columns: x => new { x.TenantId, x.ApplicationId },
                        principalSchema: "cloud",
                        principalTable: "applications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quota_buckets_capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "cloud",
                        principalTable: "capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quota_reservations",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BucketId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Actual = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_reservations", x => x.Id);
                    table.CheckConstraint("ck_quota_reservation_amounts", "\"Amount\" > 0 AND (\"Actual\" IS NULL OR (\"Actual\" >= 0 AND \"Actual\" <= \"Amount\")) AND ((\"Actual\" IS NULL) = (\"FinalizedAt\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_quota_reservations_applications_TenantId_ApplicationId",
                        columns: x => new { x.TenantId, x.ApplicationId },
                        principalSchema: "cloud",
                        principalTable: "applications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quota_reservations_quota_buckets_BucketId",
                        column: x => x.BucketId,
                        principalSchema: "cloud",
                        principalTable: "quota_buckets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quota_buckets_CapabilityId",
                schema: "cloud",
                table: "quota_buckets",
                column: "CapabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_quota_buckets_TenantId_ApplicationId_CapabilityId_Unit_Peri~",
                schema: "cloud",
                table: "quota_buckets",
                columns: new[] { "TenantId", "ApplicationId", "CapabilityId", "Unit", "PeriodStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quota_reservations_BucketId",
                schema: "cloud",
                table: "quota_reservations",
                column: "BucketId");

            migrationBuilder.CreateIndex(
                name: "IX_quota_reservations_TenantId_ApplicationId_RequestId",
                schema: "cloud",
                table: "quota_reservations",
                columns: new[] { "TenantId", "ApplicationId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quota_reservations",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "quota_buckets",
                schema: "cloud");
        }
    }
}
