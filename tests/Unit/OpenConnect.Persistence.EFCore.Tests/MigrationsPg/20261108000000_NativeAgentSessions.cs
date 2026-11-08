using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.EFCore.Tests.MigrationsPg;

/// <summary>Adds opaque native-session ciphertext storage to the PostgreSQL fixture model.</summary>
public partial class NativeAgentSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "NativeAgentSessions",
        schema: "openconnect",
        columns: table => new
        {
            BindingKey = table.Column<string>(type: "character varying(64)", unicode: false, maxLength: 64, nullable: false),
            SessionId = table.Column<string>(type: "character varying(26)", unicode: false, maxLength: 26, nullable: false),
            TenantId = table.Column<string>(type: "character varying(26)", unicode: false, maxLength: 26, nullable: false),
            PrincipalId = table.Column<string>(type: "character varying(26)", unicode: false, maxLength: 26, nullable: false),
            AgentId = table.Column<string>(type: "text", nullable: false),
            DefinitionRevision = table.Column<string>(type: "text", nullable: false),
            MafVersion = table.Column<string>(type: "text", nullable: false),
            SchemaVersion = table.Column<string>(type: "text", nullable: false),
            ProviderId = table.Column<string>(type: "text", nullable: false),
            Version = table.Column<long>(type: "bigint", nullable: false),
            Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false)
        },
        constraints: table => table.PrimaryKey("PK_NativeAgentSessions", x => x.BindingKey));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(
        name: "NativeAgentSessions", schema: "openconnect");
}
