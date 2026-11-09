using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.McpMicroservices.ServiceA.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oc_approvalnonces",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    Nonce = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    ApprovalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 256, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_approvalnonces", x => new { x.TenantId, x.Nonce });
                });

            migrationBuilder.CreateTable(
                name: "oc_approvals",
                columns: table => new
                {
                    ApprovalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Decision = table.Column<string>(type: "TEXT", nullable: true),
                    BoundArgumentsHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_approvals", x => x.ApprovalId);
                });

            migrationBuilder.CreateTable(
                name: "oc_events",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SanitizedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_events", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "oc_leases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ResourceKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    FencingVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_leases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "oc_nativeagentsessions",
                columns: table => new
                {
                    BindingKey = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    DefinitionRevision = table.Column<string>(type: "TEXT", nullable: false),
                    MafVersion = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_nativeagentsessions", x => x.BindingKey);
                });

            migrationBuilder.CreateTable(
                name: "oc_operationalruns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkloadScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FencingVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_operationalruns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "oc_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SanitizedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FencingVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "oc_privacytokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PrincipalOrWorkloadScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EncryptedValue = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: "BLOB", nullable: false),
                    WrappedDataKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    KeyProviderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    KeyVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BindingDigest = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_privacytokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "oc_publicsessionmappings",
                columns: table => new
                {
                    PublicId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    InternalSessionId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 26, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_publicsessionmappings", x => new { x.TenantId, x.PublicId });
                });

            migrationBuilder.CreateTable(
                name: "oc_sessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ContextCheckpoint = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_sessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "oc_tasks",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientTaskId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    WorkloadScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CanonicalHash = table.Column<string>(type: "TEXT", nullable: false),
                    InputRef = table.Column<string>(type: "TEXT", nullable: true),
                    ResultRef = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_tasks", x => x.TaskId);
                });

            migrationBuilder.CreateTable(
                name: "oc_turns",
                columns: table => new
                {
                    TurnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantScope = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PrincipalScope = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Context = table.Column<string>(type: "TEXT", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_turns", x => x.TurnId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oc_approvalnonces_ApprovalId",
                table: "oc_approvalnonces",
                column: "ApprovalId");

            migrationBuilder.CreateIndex(
                name: "IX_oc_approvalnonces_RequestId",
                table: "oc_approvalnonces",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_oc_approvals_TenantScope_TaskId",
                table: "oc_approvals",
                columns: new[] { "TenantScope", "TaskId" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_events_TenantScope_SessionId_Sequence",
                table: "oc_events",
                columns: new[] { "TenantScope", "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_leases_TenantScope_ResourceKey",
                table: "oc_leases",
                columns: new[] { "TenantScope", "ResourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_operationalruns_TenantScope_WorkloadScope_RunId",
                table: "oc_operationalruns",
                columns: new[] { "TenantScope", "WorkloadScope", "RunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_outbox_Status_NextAttemptUtc",
                table: "oc_outbox",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_outbox_TenantScope_EventId",
                table: "oc_outbox",
                columns: new[] { "TenantScope", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_privacytokens_ExpiresUtc",
                table: "oc_privacytokens",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_oc_privacytokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId_TurnId",
                table: "oc_privacytokens",
                columns: new[] { "TenantScope", "PrincipalOrWorkloadScope", "TokenId", "SessionId", "TurnId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_publicsessionmappings_InternalSessionId",
                table: "oc_publicsessionmappings",
                column: "InternalSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_sessions_TenantScope_PrincipalScope",
                table: "oc_sessions",
                columns: new[] { "TenantScope", "PrincipalScope" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_tasks_SessionId",
                table: "oc_tasks",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_oc_tasks_TenantScope_PrincipalScope_ClientTaskId",
                table: "oc_tasks",
                columns: new[] { "TenantScope", "PrincipalScope", "ClientTaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_turns_SessionId_Sequence",
                table: "oc_turns",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oc_approvalnonces");

            migrationBuilder.DropTable(
                name: "oc_approvals");

            migrationBuilder.DropTable(
                name: "oc_events");

            migrationBuilder.DropTable(
                name: "oc_leases");

            migrationBuilder.DropTable(
                name: "oc_nativeagentsessions");

            migrationBuilder.DropTable(
                name: "oc_operationalruns");

            migrationBuilder.DropTable(
                name: "oc_outbox");

            migrationBuilder.DropTable(
                name: "oc_privacytokens");

            migrationBuilder.DropTable(
                name: "oc_publicsessionmappings");

            migrationBuilder.DropTable(
                name: "oc_sessions");

            migrationBuilder.DropTable(
                name: "oc_tasks");

            migrationBuilder.DropTable(
                name: "oc_turns");
        }
    }
}
