namespace OpenConnect.Abstractions.Messaging;

/// <summary>Represents a verified human principal or operational agent acting within a tenant and application.</summary>
public sealed record MessagingActor(
    TenantId TenantId,
    string ApplicationId,
    ParticipantKind Kind,
    PrincipalId? PrincipalId = null,
    AgentId? AgentId = null,
    DeviceId? DeviceId = null,
    string? Issuer = null,
    string? Subject = null);

/// <summary>Represents a participant entry in a conversation.</summary>
public sealed record ConversationParticipant(
    ConversationId ConversationId,
    ParticipantKind Kind,
    PrincipalId? PrincipalId,
    AgentId? AgentId,
    ParticipantRole Role,
    ParticipantStatus Status,
    long JoinVersion,
    long? LeaveVersion = null,
    DateTimeOffset JoinedAt = default);

/// <summary>Represents an authoritative conversation record with immutable security profile.</summary>
public sealed record ConversationRecord(
    TenantId TenantId,
    string ApplicationId,
    ConversationId ConversationId,
    ConversationSecurityProfile SecurityProfile,
    ConversationState State,
    long MembershipVersion,
    long CryptoEpoch,
    long NextSequence,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ConversationParticipant> Participants);

/// <summary>Represents an authenticated encrypted message payload envelope.</summary>
public sealed record EncryptedMessageEnvelope(
    string Protocol,
    long CryptoEpoch,
    string CiphertextBase64,
    string NonceBase64,
    string AuthTagBase64,
    string? AssociatedDataBase64 = null,
    string? SenderKeyId = null);

/// <summary>Represents a durably committed message record.</summary>
public sealed record MessageRecord(
    TenantId TenantId,
    ConversationId ConversationId,
    MessageId MessageId,
    long Sequence,
    ParticipantKind SenderKind,
    PrincipalId? SenderPrincipalId,
    AgentId? SenderAgentId,
    DeviceId? SenderDeviceId,
    EncryptedMessageEnvelope Envelope,
    string ClientOperationId,
    string PayloadDigestSha256,
    DateTimeOffset CreatedAt,
    string? CorrelationId = null);

/// <summary>Represents an authenticated monotonic device receipt for a message.</summary>
public sealed record MessageReceiptRecord(
    TenantId TenantId,
    ConversationId ConversationId,
    MessageId MessageId,
    PrincipalId PrincipalId,
    DeviceId DeviceId,
    MessageReceiptKind Kind,
    long Sequence,
    DateTimeOffset RecordedAt,
    string? FailureReason = null);

/// <summary>Canonical wire event envelope for messaging realtime and replay streams.</summary>
public sealed record MessagingEventEnvelope(
    int SchemaVersion,
    string EventId,
    string Type,
    TenantId TenantId,
    ConversationId? ConversationId,
    long Sequence,
    string Cursor,
    DateTimeOffset OccurredAt,
    string PayloadJson,
    string? CorrelationId = null);

/// <summary>Represents an enrolled client or agent cryptographic device.</summary>
public sealed record DeviceRecord(
    TenantId TenantId,
    string ApplicationId,
    PrincipalId PrincipalId,
    DeviceId DeviceId,
    DevicePlatform Platform,
    string DisplayName,
    string PublicKeyBase64,
    string KeyFingerprint,
    DeviceStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? RevokedAt = null);

/// <summary>Represents a single-use published device key package for establishing E2EE membership.</summary>
public sealed record DeviceKeyPackage(
    TenantId TenantId,
    KeyPackageId KeyPackageId,
    PrincipalId? OwnerPrincipalId,
    AgentId? OwnerAgentId,
    DeviceId DeviceId,
    string Protocol,
    string PublicPackageBase64,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Consumed = false,
    DateTimeOffset? ConsumedAt = null);

/// <summary>Represents a persisted device enrollment pairing (stores only the hashed enrollment token).</summary>
public sealed record DeviceEnrollmentRecord(
    TenantId TenantId,
    string ApplicationId,
    EnrollmentId EnrollmentId,
    PrincipalId InitiatingPrincipalId,
    string Issuer,
    string Subject,
    string TokenHashSha256,
    string VerificationCode,
    EnrollmentState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DeviceId? ClaimedDeviceId = null,
    string? ClaimedDevicePublicKeyBase64 = null,
    string? ClaimedDeviceName = null,
    DevicePlatform? ClaimedPlatform = null,
    string? ChallengeNonceBase64 = null,
    DateTimeOffset? ClaimedAt = null,
    DateTimeOffset? ApprovedAt = null,
    DateTimeOffset? ConsumedAt = null);

/// <summary>QR code payload conveyed from desktop to mobile during pairing (never contains desktop tokens).</summary>
public sealed record DeviceEnrollmentQrPayload(
    int Version,
    string Type,
    string BaseGatewayUrl,
    string TenantId,
    string ApplicationId,
    string PairingId,
    string EnrollmentToken,
    DateTimeOffset ExpiresAt);

/// <summary>Result returned once upon enrollment initiation containing the single-use plaintext token for QR rendering.</summary>
public sealed record DeviceEnrollmentCreationResult(
    DeviceEnrollmentRecord Record,
    string PlaintextEnrollmentToken,
    DeviceEnrollmentQrPayload QrPayload);

/// <summary>Represents a rotating device refresh credential grant (stores only the hashed token).</summary>
public sealed record RefreshGrantRecord(
    TenantId TenantId,
    string ApplicationId,
    RefreshGrantId GrantId,
    string FamilyId,
    DeviceId DeviceId,
    PrincipalId PrincipalId,
    string Issuer,
    string Subject,
    string Audience,
    string TokenHashSha256,
    RefreshGrantStatus Status,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RotatedAt = null,
    DateTimeOffset? RevokedAt = null);

/// <summary>Short-lived device-scoped access token and rotating refresh credential issued on completion/refresh.</summary>
public sealed record MobileTokenBundle(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    RefreshGrantId RefreshGrantId,
    string FamilyId,
    DeviceId DeviceId);

/// <summary>Represents a durable voice/media call record.</summary>
public sealed record MessagingCallRecord(
    TenantId TenantId,
    string ApplicationId,
    CallId CallId,
    ConversationId ConversationId,
    ParticipantKind CallerKind,
    PrincipalId? CallerPrincipalId,
    AgentId? CallerAgentId,
    IReadOnlyList<PrincipalId> AllowedHumanRecipients,
    CallState State,
    CallMediaSecurityMode MediaSecurityMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DeviceId? AcceptedDeviceId = null,
    PrincipalId? AcceptedPrincipalId = null,
    SessionId? VoiceSessionId = null,
    string? AdmissionTokenHash = null,
    DateTimeOffset? EndedAt = null,
    string? EndReason = null);

/// <summary>Represents a durable scheduled operational follow-up.</summary>
public sealed record FollowUpRecord(
    TenantId TenantId,
    string ApplicationId,
    FollowUpId FollowUpId,
    ConversationId ConversationId,
    TaskId? TaskId,
    AgentId AgentId,
    PrincipalId RecipientPrincipalId,
    DateTimeOffset DueAtUtc,
    int Attempt,
    int MaxAttempts,
    FollowUpState State,
    string IdempotencyKey,
    string CausalEventId,
    string? LeaseOwner = null,
    long LeaseFence = 0,
    DateTimeOffset? LeaseExpiresAtUtc = null,
    TimeOnly? QuietHoursStartUtc = null,
    TimeOnly? QuietHoursEndUtc = null,
    string? TimeZoneId = null,
    DateTimeOffset? LastDispatchedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null);

/// <summary>Represents a durable continuation queued upon human reply or follow-up timeout.</summary>
public sealed record ContinuationRecord(
    TenantId TenantId,
    ContinuationId ContinuationId,
    ConversationId ConversationId,
    string TriggeringEventId,
    AgentId AgentId,
    PrincipalId RecipientPrincipalId,
    TaskId? TaskId,
    SessionId? SessionId,
    ContinuationStatus Status,
    string ExecutionIdempotencyKey,
    string? LeaseOwner = null,
    long LeaseFence = 0,
    DateTimeOffset CreatedAt = default);

/// <summary>Represents an encrypted attachment reservation/commit metadata record.</summary>
public sealed record AttachmentRecord(
    TenantId TenantId,
    AttachmentId AttachmentId,
    ConversationId ConversationId,
    string ObjectKey,
    string ObjectVersion,
    long EncryptedSizeBytes,
    string EncryptedDigestSha256,
    AttachmentState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Represents a protected device push notification registration.</summary>
public sealed record PushRegistrationRecord(
    TenantId TenantId,
    string ApplicationId,
    PushRegistrationId RegistrationId,
    PrincipalId PrincipalId,
    DeviceId DeviceId,
    PushProviderKind Provider,
    string ProtectedTokenHandle,
    bool ConsentGranted,
    PushRegistrationStatus Status,
    DateTimeOffset UpdatedAt);

/// <summary>Authorized operational agent metadata returned by agent discovery.</summary>
public sealed record OperationalAgentDiscoveryItem(
    AgentId AgentId,
    string DisplayName,
    string Description,
    string? AvatarUrl,
    IReadOnlyList<string> AllowedCapabilities,
    bool IsReady);

/// <summary>Authorization decision returned by messaging policy evaluators.</summary>
public sealed record MessagingAuthorizationDecision(
    bool Allowed,
    string? ErrorCode = null,
    string? Reason = null);

/// <summary>Signed command envelope sent to the private IMessaging Python worker.</summary>
public sealed record MessagingWorkerCommand(
    string SchemaVersion,
    string TaskId,
    string Kind,
    string TenantId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Nonce,
    string PayloadJson);

/// <summary>Validated response envelope returned from the private IMessaging Python worker.</summary>
public sealed record MessagingWorkerResult(
    string SchemaVersion,
    string TaskId,
    string Kind,
    bool Accepted,
    string? Status = null,
    string? ErrorCode = null,
    string? PayloadJson = null);

/// <summary>Verified capability readiness snapshot reported by the private IMessaging worker.</summary>
public sealed record MessagingWorkerReadiness(
    bool IsReady,
    IReadOnlyList<string> VerifiedCapabilities,
    string? ContractVersion = null,
    DateTimeOffset? CheckedAt = null);

/// <summary>Represents cryptographic group epoch state for a conversation.</summary>
public sealed record MessagingCryptoGroupState(
    ConversationId ConversationId,
    long Epoch,
    string Protocol,
    string GroupKeyHashSha256,
    IReadOnlyList<DeviceId> MemberDevices,
    DateTimeOffset UpdatedAt);

/// <summary>Represents a participant removal outcome including whether the conversation closed and active calls must terminate.</summary>
public sealed record ParticipantRemovalResult(
    ConversationRecord Conversation,
    bool ConversationClosed,
    bool TerminateActiveCalls);

/// <summary>Represents the outcome of committing a message with idempotency detection.</summary>
public sealed record MessageCommitResult(
    MessageRecord Message,
    bool WasDeduplicated);

/// <summary>Represents the outcome of resolving a due follow-up vs human reply race.</summary>
public sealed record FollowUpRaceResolution(
    FollowUpRecord FollowUp,
    bool SuppressedByHumanReply,
    ContinuationRecord? QueuedContinuation = null);
