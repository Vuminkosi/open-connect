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
            // Existing rows may hold duplicate bindings because TurnId (always null) made the old key non-unique.
            // Keep the newest binding per (tenant, principal, token, session) before enforcing uniqueness.
            migrationBuilder.Sql(@"DELETE FROM [openconnect].[PrivacyTokens] WHERE [Id] IN (SELECT [Id] FROM (SELECT [Id], ROW_NUMBER() OVER (PARTITION BY [TenantScope], [PrincipalOrWorkloadScope], [TokenId], [SessionId] ORDER BY [CreatedUtc] DESC, [Id] ASC) AS rn FROM [openconnect].[PrivacyTokens]) AS d WHERE d.rn > 1)");

            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId",
                schema: "openconnect",
                table: "PrivacyTokens");

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId",
                schema: "openconnect",
                table: "PrivacyTokens");

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId", "TurnId" },
                unique: true,
                filter: "[TurnId] IS NOT NULL");
        }
    }
}
