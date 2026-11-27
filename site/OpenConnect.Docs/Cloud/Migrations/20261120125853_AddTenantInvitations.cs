using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectIssuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.Id);
                    table.CheckConstraint("ck_cloud_invitations_role", "\"Role\" IN ('Owner', 'Administrator', 'SecurityAnalyst', 'Developer', 'BillingReader')");
                    table.ForeignKey(
                        name: "FK_invitations_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "cloud",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_TenantId",
                schema: "cloud",
                table: "invitations",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Invitation records must be preserved. Restore a coordinated backup or reconcile invitations before rollback.");
        }
    }
}
