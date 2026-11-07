using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Persistence.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class PendingModelSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "oc_turns",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "key_id",
                table: "oc_privacy_tokens",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "entity_type",
                table: "oc_privacy_tokens",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "topic",
                table: "oc_outbox",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "oc_outbox",
                type: "text",
                nullable: false,
                defaultValue: "pending",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true,
                oldDefaultValue: "pending");

            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "oc_outbox",
                type: "jsonb",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "owner",
                table: "oc_leases",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "oc_events",
                type: "jsonb",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "event_type",
                table: "oc_events",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "oc_approvals",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_turns_tenant_id_principal_id_session_id",
                table: "oc_turns",
                columns: new[] { "tenant_id", "principal_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_tasks_tenant_id_principal_id_session_id",
                table: "oc_tasks",
                columns: new[] { "tenant_id", "principal_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_approvals_tenant_id_principal_id_task_id",
                table: "oc_approvals",
                columns: new[] { "tenant_id", "principal_id", "task_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_oc_turns_tenant_id_principal_id_session_id",
                table: "oc_turns");

            migrationBuilder.DropIndex(
                name: "IX_oc_tasks_tenant_id_principal_id_session_id",
                table: "oc_tasks");

            migrationBuilder.DropIndex(
                name: "IX_oc_approvals_tenant_id_principal_id_task_id",
                table: "oc_approvals");

            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "oc_turns",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "key_id",
                table: "oc_privacy_tokens",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "entity_type",
                table: "oc_privacy_tokens",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "topic",
                table: "oc_outbox",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "oc_outbox",
                type: "text",
                nullable: true,
                defaultValue: "pending",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "pending");

            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "oc_outbox",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "owner",
                table: "oc_leases",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "payload",
                table: "oc_events",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "event_type",
                table: "oc_events",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "state",
                table: "oc_approvals",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
