using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.Postgres.Migrations;

/// <summary>Creates the initial OpenConnect PostgreSQL persistence schema.</summary>
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("oc_sessions", table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
            owner_id = table.Column<Guid>(type: "uuid", nullable: false),
            version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
            context_checkpoint = table.Column<string>(type: "jsonb", nullable: true),
            created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_oc_sessions", x => x.id);
            table.UniqueConstraint("uq_session_tenant_owner", x => new { x.tenant_id, x.owner_id, x.id });
        });

        migrationBuilder.CreateTable("oc_tasks", table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
            principal_id = table.Column<Guid>(type: "uuid", nullable: false),
            session_id = table.Column<Guid>(type: "uuid", nullable: false),
            client_task_id = table.Column<string>(type: "text", nullable: false),
            agent_id = table.Column<string>(type: "text", nullable: false),
            state = table.Column<string>(type: "text", nullable: false),
            version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
            canonical_hash = table.Column<string>(type: "text", nullable: false),
            input_ref = table.Column<string>(type: "text", nullable: true),
            result_ref = table.Column<string>(type: "text", nullable: true),
            created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_oc_tasks", x => x.id);
            table.UniqueConstraint("uq_task_tenant_principal_id", x => new { x.tenant_id, x.principal_id, x.id });
            table.ForeignKey("fk_oc_tasks_oc_sessions_tenant_id_principal_id_session_id", x => new { x.tenant_id, x.principal_id, x.session_id }, "oc_sessions", new[] { "tenant_id", "owner_id", "id" }, onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable("oc_turns", table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
            principal_id = table.Column<Guid>(type: "uuid", nullable: false),
            session_id = table.Column<Guid>(type: "uuid", nullable: false),
            sequence = table.Column<long>(type: "bigint", nullable: false),
            state = table.Column<string>(type: "text", nullable: false),
            context = table.Column<string>(type: "jsonb", nullable: true),
            started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_oc_turns", x => x.id);
            table.ForeignKey("fk_oc_turns_oc_sessions_tenant_id_principal_id_session_id", x => new { x.tenant_id, x.principal_id, x.session_id }, "oc_sessions", new[] { "tenant_id", "owner_id", "id" }, onDelete: ReferentialAction.Restrict);
        });

        migrationBuilder.CreateTable("oc_events", table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            session_id = table.Column<Guid>(type: "uuid", nullable: false),
            sequence = table.Column<long>(type: "bigint", nullable: false),
            event_type = table.Column<string>(type: "text", nullable: false),
            payload = table.Column<string>(type: "jsonb", nullable: false),
            occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            correlation_id = table.Column<string>(type: "text", nullable: true)
        }, constraints: table => table.PrimaryKey("pk_oc_events", x => x.id));

        migrationBuilder.CreateTable("oc_outbox", table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            topic = table.Column<string>(type: "text", nullable: false),
            payload = table.Column<string>(type: "jsonb", nullable: false),
            state = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
            attempts = table.Column<int>(type: "integer", nullable: false),
            next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table => table.PrimaryKey("pk_oc_outbox", x => x.id));

        migrationBuilder.CreateTable("oc_leases", table => new
        {
            name = table.Column<string>(type: "text", nullable: false),
            owner = table.Column<string>(type: "text", nullable: false),
            expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            version = table.Column<long>(type: "bigint", nullable: false)
        }, constraints: table => table.PrimaryKey("pk_oc_leases", x => x.name));

        migrationBuilder.CreateTable("oc_privacy_tokens", table => new
        {
            token = table.Column<string>(type: "text", nullable: false),
            encrypted_value = table.Column<byte[]>(type: "bytea", nullable: false),
            tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
            principal_id = table.Column<Guid>(type: "uuid", nullable: false),
            session_id = table.Column<Guid>(type: "uuid", nullable: false),
            turn_id = table.Column<Guid>(type: "uuid", nullable: true),
            entity_type = table.Column<string>(type: "text", nullable: false),
            key_id = table.Column<string>(type: "text", nullable: false),
            created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("pk_oc_privacy_tokens", x => x.token));

        migrationBuilder.CreateTable("oc_approvals", table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
            principal_id = table.Column<Guid>(type: "uuid", nullable: false),
            task_id = table.Column<Guid>(type: "uuid", nullable: false),
            state = table.Column<string>(type: "text", nullable: false),
            decision = table.Column<string>(type: "text", nullable: true),
            created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_oc_approvals", x => x.id);
            table.ForeignKey("fk_oc_approvals_oc_tasks_tenant_id_principal_id_task_id", x => new { x.tenant_id, x.principal_id, x.task_id }, "oc_tasks", new[] { "tenant_id", "principal_id", "id" }, onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateIndex("idx_sessions_tenant_owner", "oc_sessions", new[] { "tenant_id", "owner_id" });
        migrationBuilder.CreateIndex("uq_task_idempotency", "oc_tasks", new[] { "tenant_id", "principal_id", "client_task_id" }, unique: true);
        migrationBuilder.CreateIndex("idx_tasks_session", "oc_tasks", "session_id");
        migrationBuilder.CreateIndex("idx_tasks_state", "oc_tasks", "state");
        migrationBuilder.CreateIndex("uq_turn_session_sequence", "oc_turns", new[] { "session_id", "sequence" }, unique: true);
        migrationBuilder.CreateIndex("uq_event_sequence", "oc_events", new[] { "session_id", "sequence" }, unique: true);
        migrationBuilder.CreateIndex("idx_outbox_pending", "oc_outbox", new[] { "state", "next_attempt_at" }, filter: "state = 'pending'");
        migrationBuilder.CreateIndex("idx_tokens_session", "oc_privacy_tokens", "session_id");
        migrationBuilder.CreateIndex("idx_tokens_expiry", "oc_privacy_tokens", "expires_at");
        migrationBuilder.CreateIndex("ix_oc_approvals_tenant_id_principal_id_task_id", "oc_approvals", new[] { "tenant_id", "principal_id", "task_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("oc_approvals");
        migrationBuilder.DropTable("oc_events");
        migrationBuilder.DropTable("oc_leases");
        migrationBuilder.DropTable("oc_outbox");
        migrationBuilder.DropTable("oc_privacy_tokens");
        migrationBuilder.DropTable("oc_turns");
        migrationBuilder.DropTable("oc_tasks");
        migrationBuilder.DropTable("oc_sessions");
    }
}
