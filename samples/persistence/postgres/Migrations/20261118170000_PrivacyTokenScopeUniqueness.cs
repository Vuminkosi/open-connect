using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyTokenScopeUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Validate uniqueness before removing the existing index; never choose or delete bindings.
            migrationBuilder.CreateIndex(
                name: "UX_PrivacyTokens_ScopeUpgrade",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~",
                schema: "openconnect",
                table: "PrivacyTokens");

            migrationBuilder.RenameIndex(
                name: "UX_PrivacyTokens_ScopeUpgrade",
                schema: "openconnect",
                table: "PrivacyTokens",
                newName: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the old index before removing the new one; all bindings are preserved.
            migrationBuilder.CreateIndex(
                name: "UX_PrivacyTokens_ScopeRollback",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId", "TurnId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~",
                schema: "openconnect",
                table: "PrivacyTokens");

            migrationBuilder.RenameIndex(
                name: "UX_PrivacyTokens_ScopeRollback",
                schema: "openconnect",
                table: "PrivacyTokens",
                newName: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~");
        }
    }
}
