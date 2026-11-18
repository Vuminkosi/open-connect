using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PrivacyTokenScopeUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Validate uniqueness before removing the existing index; never choose or delete bindings.
            migrationBuilder.CreateIndex(
                name: "IX_oc_privacytokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId",
                table: "oc_privacytokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_oc_privacytokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId",
                table: "oc_privacytokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the old index before removing the new one; all bindings are preserved.
            migrationBuilder.CreateIndex(
                name: "IX_oc_privacytokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId",
                table: "oc_privacytokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId", "TurnId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_oc_privacytokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId",
                table: "oc_privacytokens");
        }
    }
}
