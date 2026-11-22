using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Docs.Cloud.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudCapabilityCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capabilities",
                schema: "cloud",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContractVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BillingUnit = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PlatformAvailability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Dependencies = table.Column<string>(type: "jsonb", nullable: false),
                    Modalities = table.Column<string>(type: "jsonb", nullable: false),
                    AllowedRegions = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capabilities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "application_capability_selections",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_application_capability_selections", x => new { x.TenantId, x.ApplicationId, x.CapabilityId });
                    table.ForeignKey(
                        name: "FK_application_capability_selections_applications_TenantId_App~",
                        columns: x => new { x.TenantId, x.ApplicationId },
                        principalSchema: "cloud",
                        principalTable: "applications",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_application_capability_selections_capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "cloud",
                        principalTable: "capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_capability_selections",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_capability_selections", x => new { x.TenantId, x.CapabilityId });
                    table.ForeignKey(
                        name: "FK_tenant_capability_selections_capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "cloud",
                        principalTable: "capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_capability_selections_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "cloud",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_entitlements",
                schema: "cloud",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapabilityId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PlanGranted = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_entitlements", x => new { x.TenantId, x.CapabilityId });
                    table.ForeignKey(
                        name: "FK_tenant_entitlements_capabilities_CapabilityId",
                        column: x => x.CapabilityId,
                        principalSchema: "cloud",
                        principalTable: "capabilities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_entitlements_tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "cloud",
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "cloud",
                table: "capabilities",
                columns: new[] { "Id", "Name", "Description", "ContractVersion", "BillingUnit", "PlatformAvailability", "Dependencies", "Modalities", "AllowedRegions" },
                values: new object[,]
                {
                    { "inference.chat", "AI inference", "Text-oriented model inference service.", "v1", "input/output tokens", "not_configured", "[]", "[]", "[]" },
                    { "inference.liveui", "LiveUI planning", "Constrained LiveUI plan generation; browser actions remain in the application host.", "v1", "plans and tokens", "not_configured", "[]", "[]", "[]" },
                    { "classification.classify", "Classification", "Classification service.", "v1", "requests and model compute", "not_configured", "[]", "[]", "[]" },
                    { "security.inspect", "Security inspection", "Enhanced managed security inspection; application-host privacy enforcement remains independent.", "v1", "inspections", "not_configured", "[]", "[]", "[]" },
                    { "voice.synthesize", "Speech synthesis", "Speech synthesis service.", "v1", "audio duration or characters", "not_configured", "[]", "[]", "[]" },
                    { "voice.realtime", "Realtime voice", "Realtime voice session composed from explicitly entitled capabilities.", "v1", "sessions and stream duration", "not_configured", "[\"speech.transcribe\",\"voice.synthesize\",\"inference.chat\"]", "[]", "[]" },
                    { "speech.transcribe", "Transcription", "Speech transcription service.", "v1", "audio seconds", "not_configured", "[]", "[]", "[]" },
                    { "translation.translate", "Translation", "Translation service.", "v1", "characters or tokens", "not_configured", "[]", "[]", "[]" },
                    { "navigator.execute", "Assisted browser automation", "Separately gated browser automation service.", "v1", "browser seconds or jobs", "not_configured", "[]", "[]", "[]" },
                    { "agents.execute", "Hosted operational agents", "Separately gated hosted operational agent service.", "v1", "steps or CPU time", "not_configured", "[]", "[]", "[]" },
                    { "outbox.publish", "Durable outgoing delivery", "Durable outgoing delivery service.", "v1", "messages or deliveries", "not_configured", "[]", "[]", "[]" },
                });

            migrationBuilder.CreateIndex(
                name: "IX_application_capability_selections_CapabilityId",
                schema: "cloud",
                table: "application_capability_selections",
                column: "CapabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_capability_selections_CapabilityId",
                schema: "cloud",
                table: "tenant_capability_selections",
                column: "CapabilityId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_entitlements_CapabilityId",
                schema: "cloud",
                table: "tenant_entitlements",
                column: "CapabilityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "application_capability_selections",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "tenant_capability_selections",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "tenant_entitlements",
                schema: "cloud");

            migrationBuilder.DropTable(
                name: "capabilities",
                schema: "cloud");
        }
    }
}
