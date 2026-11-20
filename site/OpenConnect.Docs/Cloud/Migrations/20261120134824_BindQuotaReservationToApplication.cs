using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class BindQuotaReservationToApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_quota_reservations_quota_buckets_BucketId",
                schema: "cloud",
                table: "quota_reservations");

            migrationBuilder.DropIndex(
                name: "IX_quota_reservations_BucketId",
                schema: "cloud",
                table: "quota_reservations");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_quota_buckets_TenantId_ApplicationId_Id",
                schema: "cloud",
                table: "quota_buckets",
                columns: new[] { "TenantId", "ApplicationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_quota_reservations_TenantId_ApplicationId_BucketId",
                schema: "cloud",
                table: "quota_reservations",
                columns: new[] { "TenantId", "ApplicationId", "BucketId" });

            migrationBuilder.AddForeignKey(
                name: "FK_quota_reservations_quota_buckets_TenantId_ApplicationId_Buc~",
                schema: "cloud",
                table: "quota_reservations",
                columns: new[] { "TenantId", "ApplicationId", "BucketId" },
                principalSchema: "cloud",
                principalTable: "quota_buckets",
                principalColumns: new[] { "TenantId", "ApplicationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_quota_reservations_quota_buckets_TenantId_ApplicationId_Buc~",
                schema: "cloud",
                table: "quota_reservations");

            migrationBuilder.DropIndex(
                name: "IX_quota_reservations_TenantId_ApplicationId_BucketId",
                schema: "cloud",
                table: "quota_reservations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_quota_buckets_TenantId_ApplicationId_Id",
                schema: "cloud",
                table: "quota_buckets");

            migrationBuilder.CreateIndex(
                name: "IX_quota_reservations_BucketId",
                schema: "cloud",
                table: "quota_reservations",
                column: "BucketId");

            migrationBuilder.AddForeignKey(
                name: "FK_quota_reservations_quota_buckets_BucketId",
                schema: "cloud",
                table: "quota_reservations",
                column: "BucketId",
                principalSchema: "cloud",
                principalTable: "quota_buckets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
