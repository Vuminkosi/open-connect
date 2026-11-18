using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.EFCore.Tests.MigrationsPg
{
    /// <inheritdoc />
    public partial class PrivacyTokenScopeUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~",
                schema: "openconnect",
                table: "PrivacyTokens");

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~",
                schema: "openconnect",
                table: "PrivacyTokens");

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId", "TurnId" },
                unique: true);
        }
    }
}
