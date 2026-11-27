using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipRevisionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "membership_revisions",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastRevision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_revisions", x => new { x.TenantId, x.SubjectHash });
                    table.ForeignKey(
                        name: "FK_membership_revisions_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "cloud",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Membership revision history must be preserved. Restore a coordinated backup or reconcile history before rollback.");
        }
    }
}
