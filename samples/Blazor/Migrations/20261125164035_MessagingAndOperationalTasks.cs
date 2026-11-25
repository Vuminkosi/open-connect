using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenConnect.Samples.Blazor.Migrations
{
    /// <inheritdoc />
    public partial class MessagingAndOperationalTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalsJson",
                table: "todo_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedPrincipalId",
                table: "todo_items",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuditHistoryJson",
                table: "todo_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommentsJson",
                table: "todo_items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscalationPolicy",
                table: "todo_items",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "todo_items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "todo_items",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptPayload",
                table: "oc_tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptStatus",
                table: "oc_tasks",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandDigest",
                table: "oc_approvals",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommandId",
                table: "oc_approvals",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConsumedAtUtc",
                table: "oc_approvals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "oc_messagingconversations",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SecurityProfile = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MembershipVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CryptoEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    NextSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingconversations", x => new { x.TenantId, x.ApplicationId, x.ConversationId });
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingdevices",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PublicKeyBase64 = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    KeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingdevices", x => new { x.TenantId, x.ApplicationId, x.DeviceId });
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingenrollments",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EnrollmentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    InitiatingPrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TokenHashSha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    VerificationCode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimedDeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimedDevicePublicKeyBase64 = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ClaimedDeviceName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ClaimedPlatform = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ChallengeNonceBase64 = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConsumedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingenrollments", x => new { x.TenantId, x.ApplicationId, x.EnrollmentId });
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingkeypackages",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    KeyPackageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OwnerAgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublicPackageBase64 = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingkeypackages", x => new { x.TenantId, x.ApplicationId, x.KeyPackageId });
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingpushregistrations",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RegistrationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProtectedTokenHandle = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ConsentGranted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingpushregistrations", x => new { x.TenantId, x.ApplicationId, x.RegistrationId });
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingrefreshgrants",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GrantId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FamilyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Audience = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TokenHashSha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RotatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingrefreshgrants", x => new { x.TenantId, x.ApplicationId, x.GrantId });
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingattachments",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AttachmentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ObjectVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EncryptedSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    EncryptedDigestSha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingattachments", x => new { x.TenantId, x.ApplicationId, x.AttachmentId });
                    table.ForeignKey(
                        name: "FK_oc_messagingattachments_oc_messagingconversations_TenantId_ApplicationId_ConversationId",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ConversationId },
                        principalTable: "oc_messagingconversations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "ConversationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingcalls",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CallId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CallerKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CallerPrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CallerAgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AllowedHumanRecipientsJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MediaSecurityMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedDeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AcceptedPrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    VoiceSessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AdmissionTokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndReason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingcalls", x => new { x.TenantId, x.ApplicationId, x.CallId });
                    table.ForeignKey(
                        name: "FK_oc_messagingcalls_oc_messagingconversations_TenantId_ApplicationId_ConversationId",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ConversationId },
                        principalTable: "oc_messagingconversations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "ConversationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingcontinuations",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContinuationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TriggeringEventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RecipientPrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExecutionIdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LeaseFence = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingcontinuations", x => new { x.TenantId, x.ApplicationId, x.ContinuationId });
                    table.ForeignKey(
                        name: "FK_oc_messagingcontinuations_oc_messagingconversations_TenantId_ApplicationId_ConversationId",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ConversationId },
                        principalTable: "oc_messagingconversations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "ConversationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingfollowups",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FollowUpId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RecipientPrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CausalEventId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    LeaseFence = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QuietHoursStartUtc = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    QuietHoursEndUtc = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastDispatchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingfollowups", x => new { x.TenantId, x.ApplicationId, x.FollowUpId });
                    table.ForeignKey(
                        name: "FK_oc_messagingfollowups_oc_messagingconversations_TenantId_ApplicationId_ConversationId",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ConversationId },
                        principalTable: "oc_messagingconversations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "ConversationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingmessages",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    SenderKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SenderPrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SenderAgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SenderDeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SenderKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CryptoEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    CiphertextBase64 = table.Column<string>(type: "TEXT", nullable: false),
                    NonceBase64 = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AuthTagBase64 = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AssociatedDataBase64 = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SenderKeyId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ClientOperationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PayloadDigestSha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingmessages", x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_oc_messagingmessages_oc_messagingconversations_TenantId_ApplicationId_ConversationId",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ConversationId },
                        principalTable: "oc_messagingconversations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "ConversationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingparticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ParticipantKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    JoinVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaveVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    JoinedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingparticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_oc_messagingparticipants_oc_messagingconversations_TenantId_ApplicationId_ConversationId",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ConversationId },
                        principalTable: "oc_messagingconversations",
                        principalColumns: new[] { "TenantId", "ApplicationId", "ConversationId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oc_messagingreceipts",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oc_messagingreceipts", x => new { x.TenantId, x.ApplicationId, x.MessageId, x.DeviceId });
                    table.ForeignKey(
                        name: "FK_oc_messagingreceipts_oc_messagingmessages_TenantId_ApplicationId_ConversationId_MessageId",
                        columns: x => new { x.TenantId, x.ApplicationId, x.ConversationId, x.MessageId },
                        principalTable: "oc_messagingmessages",
                        principalColumns: new[] { "TenantId", "ApplicationId", "ConversationId", "MessageId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_oc_approvals_TenantScope_PrincipalScope_TaskId_CommandId",
                table: "oc_approvals",
                columns: new[] { "TenantScope", "PrincipalScope", "TaskId", "CommandId" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingattachments_TenantId_ApplicationId_ConversationId_State",
                table: "oc_messagingattachments",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingattachments_TenantId_ApplicationId_ObjectKey_ObjectVersion",
                table: "oc_messagingattachments",
                columns: new[] { "TenantId", "ApplicationId", "ObjectKey", "ObjectVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingcalls_TenantId_ApplicationId_ConversationId_State",
                table: "oc_messagingcalls",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingcalls_TenantId_CallId",
                table: "oc_messagingcalls",
                columns: new[] { "TenantId", "CallId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingcontinuations_TenantId_ApplicationId_ConversationId_Status",
                table: "oc_messagingcontinuations",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingcontinuations_TenantId_ApplicationId_ExecutionIdempotencyKey",
                table: "oc_messagingcontinuations",
                columns: new[] { "TenantId", "ApplicationId", "ExecutionIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingconversations_TenantId_ApplicationId_State",
                table: "oc_messagingconversations",
                columns: new[] { "TenantId", "ApplicationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingconversations_TenantId_ConversationId",
                table: "oc_messagingconversations",
                columns: new[] { "TenantId", "ConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingdevices_TenantId_ApplicationId_PrincipalId_Status",
                table: "oc_messagingdevices",
                columns: new[] { "TenantId", "ApplicationId", "PrincipalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingdevices_TenantId_DeviceId",
                table: "oc_messagingdevices",
                columns: new[] { "TenantId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingenrollments_TenantId_ApplicationId_InitiatingPrincipalId_State",
                table: "oc_messagingenrollments",
                columns: new[] { "TenantId", "ApplicationId", "InitiatingPrincipalId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingenrollments_TenantId_EnrollmentId",
                table: "oc_messagingenrollments",
                columns: new[] { "TenantId", "EnrollmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingenrollments_TokenHashSha256",
                table: "oc_messagingenrollments",
                column: "TokenHashSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingfollowups_State_DueAtUtc_LeaseExpiresAtUtc",
                table: "oc_messagingfollowups",
                columns: new[] { "State", "DueAtUtc", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingfollowups_TenantId_ApplicationId_ConversationId_RecipientPrincipalId_State",
                table: "oc_messagingfollowups",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "RecipientPrincipalId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingfollowups_TenantId_ApplicationId_IdempotencyKey",
                table: "oc_messagingfollowups",
                columns: new[] { "TenantId", "ApplicationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingfollowups_TenantId_FollowUpId",
                table: "oc_messagingfollowups",
                columns: new[] { "TenantId", "FollowUpId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingkeypackages_TenantId_ApplicationId_DeviceId_Consumed_ExpiresAtUtc",
                table: "oc_messagingkeypackages",
                columns: new[] { "TenantId", "ApplicationId", "DeviceId", "Consumed", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingmessages_TenantId_ApplicationId_ConversationId_SenderKey_ClientOperationId",
                table: "oc_messagingmessages",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "SenderKey", "ClientOperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingmessages_TenantId_ApplicationId_ConversationId_Sequence",
                table: "oc_messagingmessages",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingmessages_TenantId_ConversationId_ClientOperationId",
                table: "oc_messagingmessages",
                columns: new[] { "TenantId", "ConversationId", "ClientOperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingmessages_TenantId_ConversationId_MessageId",
                table: "oc_messagingmessages",
                columns: new[] { "TenantId", "ConversationId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingparticipants_TenantId_ApplicationId_AgentId_Status",
                table: "oc_messagingparticipants",
                columns: new[] { "TenantId", "ApplicationId", "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingparticipants_TenantId_ApplicationId_ConversationId_ParticipantKey",
                table: "oc_messagingparticipants",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "ParticipantKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingparticipants_TenantId_ApplicationId_PrincipalId_Status",
                table: "oc_messagingparticipants",
                columns: new[] { "TenantId", "ApplicationId", "PrincipalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingpushregistrations_TenantId_ApplicationId_DeviceId_Status",
                table: "oc_messagingpushregistrations",
                columns: new[] { "TenantId", "ApplicationId", "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingpushregistrations_TenantId_ApplicationId_PrincipalId_Status",
                table: "oc_messagingpushregistrations",
                columns: new[] { "TenantId", "ApplicationId", "PrincipalId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingreceipts_TenantId_ApplicationId_ConversationId_MessageId",
                table: "oc_messagingreceipts",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingreceipts_TenantId_ApplicationId_ConversationId_PrincipalId",
                table: "oc_messagingreceipts",
                columns: new[] { "TenantId", "ApplicationId", "ConversationId", "PrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingreceipts_TenantId_MessageId_DeviceId",
                table: "oc_messagingreceipts",
                columns: new[] { "TenantId", "MessageId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingrefreshgrants_TenantId_ApplicationId_DeviceId_Status",
                table: "oc_messagingrefreshgrants",
                columns: new[] { "TenantId", "ApplicationId", "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingrefreshgrants_TenantId_ApplicationId_FamilyId_Status",
                table: "oc_messagingrefreshgrants",
                columns: new[] { "TenantId", "ApplicationId", "FamilyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_oc_messagingrefreshgrants_TokenHashSha256",
                table: "oc_messagingrefreshgrants",
                column: "TokenHashSha256",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oc_messagingattachments");

            migrationBuilder.DropTable(
                name: "oc_messagingcalls");

            migrationBuilder.DropTable(
                name: "oc_messagingcontinuations");

            migrationBuilder.DropTable(
                name: "oc_messagingdevices");

            migrationBuilder.DropTable(
                name: "oc_messagingenrollments");

            migrationBuilder.DropTable(
                name: "oc_messagingfollowups");

            migrationBuilder.DropTable(
                name: "oc_messagingkeypackages");

            migrationBuilder.DropTable(
                name: "oc_messagingparticipants");

            migrationBuilder.DropTable(
                name: "oc_messagingpushregistrations");

            migrationBuilder.DropTable(
                name: "oc_messagingreceipts");

            migrationBuilder.DropTable(
                name: "oc_messagingrefreshgrants");

            migrationBuilder.DropTable(
                name: "oc_messagingmessages");

            migrationBuilder.DropTable(
                name: "oc_messagingconversations");

            migrationBuilder.DropIndex(
                name: "IX_oc_approvals_TenantScope_PrincipalScope_TaskId_CommandId",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "ApprovalsJson",
                table: "todo_items");

            migrationBuilder.DropColumn(
                name: "AssignedPrincipalId",
                table: "todo_items");

            migrationBuilder.DropColumn(
                name: "AuditHistoryJson",
                table: "todo_items");

            migrationBuilder.DropColumn(
                name: "CommentsJson",
                table: "todo_items");

            migrationBuilder.DropColumn(
                name: "EscalationPolicy",
                table: "todo_items");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "todo_items");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "todo_items");

            migrationBuilder.DropColumn(
                name: "ReceiptPayload",
                table: "oc_tasks");

            migrationBuilder.DropColumn(
                name: "ReceiptStatus",
                table: "oc_tasks");

            migrationBuilder.DropColumn(
                name: "CommandDigest",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "CommandId",
                table: "oc_approvals");

            migrationBuilder.DropColumn(
                name: "ConsumedAtUtc",
                table: "oc_approvals");
        }
    }
}
