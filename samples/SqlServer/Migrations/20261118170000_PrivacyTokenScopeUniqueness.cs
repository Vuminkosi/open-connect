using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyTokenScopeUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Validate uniqueness before removing the existing index; never choose or delete bindings.
            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId",
                schema: "openconnect",
                table: "PrivacyTokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the old index before removing the new one; all bindings are preserved.
            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId", "TurnId" },
                unique: true,
                filter: "[TurnId] IS NOT NULL");

            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId",
                schema: "openconnect",
                table: "PrivacyTokens");
        }
    }
}
