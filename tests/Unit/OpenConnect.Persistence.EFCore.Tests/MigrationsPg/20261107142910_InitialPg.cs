using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.EFCore.Tests.MigrationsPg
{
    /// <inheritdoc />
    public partial class InitialPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "openconnect");

            migrationBuilder.CreateTable(
                name: "Approvals",
                schema: "openconnect",
                columns: table => new
                {
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: true),
                    BoundArgumentsHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Approvals", x => x.ApprovalId);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                schema: "openconnect",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SanitizedPayload = table.Column<string>(type: "text", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Leases",
                schema: "openconnect",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ResourceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Owner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FencingVersion = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationalRuns",
                schema: "openconnect",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WorkloadScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RunId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FencingVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                schema: "openconnect",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SanitizedPayload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FencingVersion = table.Column<long>(type: "bigint", nullable: false),
                    LeaseOwner = table.Column<string>(type: "text", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrivacyTokens",
                schema: "openconnect",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalOrWorkloadScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    TokenId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EncryptedValue = table.Column<byte[]>(type: "bytea", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: "bytea", nullable: false),
                    WrappedDataKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    KeyProviderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    KeyVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BindingDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "openconnect",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ContextCheckpoint = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                schema: "openconnect",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientTaskId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WorkloadScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CanonicalHash = table.Column<string>(type: "text", nullable: false),
                    InputRef = table.Column<string>(type: "text", nullable: true),
                    ResultRef = table.Column<string>(type: "text", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.TaskId);
                });

            migrationBuilder.CreateTable(
                name: "Turns",
                schema: "openconnect",
                columns: table => new
                {
                    TurnId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantScope = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Context = table.Column<string>(type: "text", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Turns", x => x.TurnId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_TenantScope_TaskId",
                schema: "openconnect",
                table: "Approvals",
                columns: new[] { "TenantScope", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TenantScope_SessionId_Sequence",
                schema: "openconnect",
                table: "Events",
                columns: new[] { "TenantScope", "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leases_TenantScope_ResourceKey",
                schema: "openconnect",
                table: "Leases",
                columns: new[] { "TenantScope", "ResourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalRuns_TenantScope_WorkloadScope_RunId",
                schema: "openconnect",
                table: "OperationalRuns",
                columns: new[] { "TenantScope", "WorkloadScope", "RunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Status_NextAttemptUtc",
                schema: "openconnect",
                table: "Outbox",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_TenantScope_EventId",
                schema: "openconnect",
                table: "Outbox",
                columns: new[] { "TenantScope", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_ExpiresUtc",
                schema: "openconnect",
                table: "PrivacyTokens",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyTokens_TenantScope_PrincipalOrWorkloadScope_TokenId_~",
                schema: "openconnect",
                table: "PrivacyTokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId", "TurnId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TenantScope_PrincipalScope",
                schema: "openconnect",
                table: "Sessions",
                columns: new[] { "TenantScope", "PrincipalScope" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SessionId",
                schema: "openconnect",
                table: "Tasks",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TenantScope_PrincipalScope_ClientTaskId",
                schema: "openconnect",
                table: "Tasks",
                columns: new[] { "TenantScope", "PrincipalScope", "ClientTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Turns_SessionId_Sequence",
                schema: "openconnect",
                table: "Turns",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Approvals",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "Events",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "Leases",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "OperationalRuns",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Outbox",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "PrivacyTokens",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "Tasks",
                schema: "openconnect");

            migrationBuilder.DropTable(
                name: "Turns",
                schema: "openconnect");
        }
    }
}
