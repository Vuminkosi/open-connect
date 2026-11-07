using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class NavigatorDurability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_oc_leases",
                table: "oc_leases");

            migrationBuilder.DropIndex(
                name: "uq_event_sequence",
                table: "oc_events");

            migrationBuilder.DropIndex(
                name: "IX_oc_approvals_tenant_id_principal_id_task_id",
                table: "oc_approvals");

            migrationBuilder.AddColumn<string>(
                name: "receipt_payload",
                table: "oc_tasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_status",
                table: "oc_tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "oc_leases",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "principal_id",
                table: "oc_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "oc_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "command_digest",
                table: "oc_approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "command_id",
                table: "oc_approvals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "consumed_at",
                table: "oc_approvals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "oc_approvals",
                type: "timestamp with time zone",
                nullable: true);

            // Fail closed if the reserved identity is already owned by a non-quarantine
            // session. Do not silently reuse or modify real data; remove/resolve the
            // conflicting session after inspecting its events, then retry the migration.
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM oc_sessions AS s
                        WHERE s.id = '00000000-0000-0000-0000-000000000001'::uuid
                          AND (
                              s.tenant_id IS DISTINCT FROM '00000000-0000-0000-0000-000000000000'::uuid
                              OR s.owner_id IS DISTINCT FROM '00000000-0000-0000-0000-000000000000'::uuid
                              OR s.context_checkpoint IS DISTINCT FROM '{"quarantine":true}'::jsonb
                          )
                    ) THEN
                        RAISE EXCEPTION 'Navigator quarantine sentinel ID is occupied by a session that does not match the reserved identity (zero tenant, zero owner, and quarantine checkpoint). Inspect and safely resolve that session and its events, then retry the migration; do not reuse the sentinel.';
                    END IF;
                END
                $migration$;
                """);

            // Stable reserved identity for quarantined legacy events. This row uses the
            // zero tenant/principal namespace; ordinary application-created sessions must
            // not use this ID. Keep orphan payloads and their former session IDs recoverable.
            migrationBuilder.Sql("""
                INSERT INTO oc_sessions (id, tenant_id, owner_id, version, context_checkpoint, created_at, updated_at, expires_at)
                SELECT '00000000-0000-0000-0000-000000000001'::uuid,
                       '00000000-0000-0000-0000-000000000000'::uuid,
                       '00000000-0000-0000-0000-000000000000'::uuid,
                       1, '{"quarantine":true}'::jsonb, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, NULL
                WHERE EXISTS (
                    SELECT 1 FROM oc_events AS e
                    WHERE NOT EXISTS (SELECT 1 FROM oc_sessions AS s WHERE s.id = e.session_id)
                )
                ON CONFLICT (id) DO NOTHING;
                """);

            // Legacy events inherit immutable ownership from a matching session.
            // The zero-tenant lease default preserves existing global leases as a distinct
            // legacy namespace; new callers must always supply the real tenant identifier.
            migrationBuilder.Sql("""
                UPDATE oc_events AS e
                SET tenant_id = s.tenant_id, principal_id = s.owner_id
                FROM oc_sessions AS s
                WHERE s.id = e.session_id;
                """);

            // If a legacy payload already has this metadata key, preserve its value. The
            // prior value cannot safely be replaced; the event is still quarantined, but
            // its original session ID is not recorded by this migration in that case.
            migrationBuilder.Sql("""
                UPDATE oc_events AS e
                SET payload = CASE
                        WHEN e.payload ? '_navigator_quarantine_original_session_id' THEN e.payload
                        ELSE jsonb_set(e.payload, '{_navigator_quarantine_original_session_id}', to_jsonb(e.session_id::text), true)
                    END,
                    session_id = '00000000-0000-0000-0000-000000000001'::uuid,
                    tenant_id = '00000000-0000-0000-0000-000000000000'::uuid,
                    principal_id = '00000000-0000-0000-0000-000000000000'::uuid
                WHERE e.tenant_id = '00000000-0000-0000-0000-000000000000'::uuid
                  AND e.principal_id = '00000000-0000-0000-0000-000000000000'::uuid
                  AND NOT EXISTS (SELECT 1 FROM oc_sessions AS s WHERE s.id = e.session_id);
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_oc_leases",
                table: "oc_leases",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "uq_event_sequence",
                table: "oc_events",
                columns: new[] { "tenant_id", "principal_id", "session_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_approvals_pending",
                table: "oc_approvals",
                columns: new[] { "tenant_id", "principal_id", "state", "expires_at" },
                filter: "state = 'pending'");

            migrationBuilder.CreateIndex(
                name: "uq_approval_command",
                table: "oc_approvals",
                columns: new[] { "tenant_id", "principal_id", "task_id", "command_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_oc_events_oc_sessions_tenant_id_principal_id_session_id",
                table: "oc_events",
                columns: new[] { "tenant_id", "principal_id", "session_id" },
                principalTable: "oc_sessions",
                principalColumns: new[] { "tenant_id", "owner_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oc_events_oc_sessions_tenant_id_principal_id_session_id",
                table: "oc_events");

            // Quarantine events are retained through rollback, so the sentinel normally
            // remains referenced and is preserved. Legacy ownership backfills and event
            // quarantine metadata cannot be losslessly reversed after application writes.
            migrationBuilder.Sql("""
                DELETE FROM oc_sessions AS s
                WHERE s.id = '00000000-0000-0000-0000-000000000001'::uuid
                  AND NOT EXISTS (SELECT 1 FROM oc_events AS e WHERE e.session_id = s.id);
                """);

            migrationBuilder.DropPrimaryKey(
                name: "PK_oc_leases",
                table: "oc_leases");

            migrationBuilder.DropIndex(
                name: "uq_event_sequence",
                table: "oc_events");

            migrationBuilder.DropIndex(
                name: "idx_approvals_pending",
                table: "oc_approvals");

            migrationBuilder.DropIndex(
                name: "uq_approval_command",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "receipt_payload",
                table: "oc_tasks");

            migrationBuilder.DropColumn(
                name: "receipt_status",
                table: "oc_tasks");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oc_leases");

            migrationBuilder.DropColumn(
                name: "principal_id",
                table: "oc_events");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oc_events");

            migrationBuilder.DropColumn(
                name: "command_digest",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "command_id",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "consumed_at",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "oc_approvals");

            migrationBuilder.AddPrimaryKey(
                name: "PK_oc_leases",
                table: "oc_leases",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "uq_event_sequence",
                table: "oc_events",
                columns: new[] { "session_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_approvals_tenant_id_principal_id_task_id",
                table: "oc_approvals",
                columns: new[] { "tenant_id", "principal_id", "task_id" });
        }
    }
}
