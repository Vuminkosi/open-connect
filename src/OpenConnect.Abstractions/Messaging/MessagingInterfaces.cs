namespace OpenConnect.Abstractions.Messaging;

/// <summary>Resolves authorized human or agent recipients within a tenant and application (default deny).</summary>
public interface IMessagingRecipientResolver
{
    ValueTask<MessagingActor?> ResolveHumanAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingActor?> ResolveAgentAsync(
        TenantId tenantId,
        string applicationId,
        AgentId agentId,
        CancellationToken cancellationToken = default);
}

/// <summary>Evaluates fail-closed authorization for every messaging operation.</summary>
public interface IMessagingAuthorizer
{
    ValueTask<MessagingAuthorizationDecision> AuthorizeAsync(
        MessagingActor actor,
        MessagingPermission permission,
        ConversationId? conversationId = null,
        MessagingActor? target = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Filters operational agents authorized for discovery by a verified human principal.</summary>
public interface IAgentDiscoveryAuthorizer
{
    ValueTask<IReadOnlyList<OperationalAgentDiscoveryItem>> GetAuthorizedAgentsAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default);
}

/// <summary>Evaluates policy and recent MFA/auth requirements for device enrollment and revocation.</summary>
public interface IDeviceEnrollmentAuthorizer
{
    ValueTask<MessagingAuthorizationDecision> AuthorizeInitiateAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingAuthorizationDecision> AuthorizeConfirmAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingAuthorizationDecision> AuthorizeRevokeAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Issues and rotates device-scoped mobile access and refresh credentials.</summary>
public interface IMobileTokenIssuer
{
    ValueTask<MobileTokenBundle> IssueAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        string issuer,
        string subject,
        string audience,
        CancellationToken cancellationToken = default);

    ValueTask<MobileTokenBundle> RotateAsync(
        RefreshGrantRecord existingGrant,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable persistence seam for conversations, participants, messages, and receipts.</summary>
public interface IMessagingConversationStore
{
    ValueTask<ConversationRecord> CreateConversationAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationRecord?> GetConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationRecord> UpdateConversationAsync(
        ConversationRecord conversation,
        long expectedMembershipVersion,
        CancellationToken cancellationToken = default);

    ValueTask<MessageRecord?> GetMessageByClientOperationIdAsync(
        TenantId tenantId,
        ConversationId conversationId,
        string clientOperationId,
        CancellationToken cancellationToken = default);

    ValueTask<MessageRecord> CommitMessageAsync(
        MessageRecord message,
        CancellationToken cancellationToken = default);

    ValueTask<MessageRecord?> GetMessageAsync(
        TenantId tenantId,
        ConversationId conversationId,
        MessageId messageId,
        CancellationToken cancellationToken = default);

    ValueTask<MessageReceiptRecord?> GetReceiptAsync(
        TenantId tenantId,
        MessageId messageId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<MessageReceiptRecord> UpsertReceiptAsync(
        MessageReceiptRecord receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable persistence seam for device enrollments, enrolled devices, refresh grants, key packages, and push registrations.</summary>
public interface IDeviceEnrollmentStore
{
    ValueTask<DeviceEnrollmentRecord> CreateEnrollmentAsync(
        DeviceEnrollmentRecord enrollment,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentRecord?> GetEnrollmentAsync(
        TenantId tenantId,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentRecord?> GetEnrollmentByTokenHashAsync(
        string tokenHashSha256,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryUpdateEnrollmentAsync(
        DeviceEnrollmentRecord updated,
        EnrollmentState expectedState,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceRecord> UpsertDeviceAsync(
        DeviceRecord device,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceRecord?> GetDeviceAsync(
        TenantId tenantId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DeviceRecord>> ListDevicesAsync(
        TenantId tenantId,
        PrincipalId principalId,
        CancellationToken cancellationToken = default);

    ValueTask<RefreshGrantRecord> CreateRefreshGrantAsync(
        RefreshGrantRecord grant,
        CancellationToken cancellationToken = default);

    ValueTask<RefreshGrantRecord?> GetRefreshGrantByTokenHashAsync(
        string tokenHashSha256,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryUpdateRefreshGrantAsync(
        RefreshGrantRecord updated,
        RefreshGrantStatus expectedStatus,
        CancellationToken cancellationToken = default);

    ValueTask RevokeRefreshFamilyAsync(
        TenantId tenantId,
        string familyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);

    ValueTask RevokeAllDeviceRefreshGrantsAsync(
        TenantId tenantId,
        DeviceId deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);

    ValueTask RevokeDevicePushRegistrationsAsync(
        TenantId tenantId,
        DeviceId deviceId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable persistence seam for voice/media calls with CAS state transitions.</summary>
public interface IMessagingCallStore
{
    ValueTask<MessagingCallRecord> CreateCallAsync(
        MessagingCallRecord call,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord?> GetCallAsync(
        TenantId tenantId,
        CallId callId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MessagingCallRecord>> GetActiveCallsForConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryTransitionCallAsync(
        MessagingCallRecord updated,
        CallState expectedState,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable persistence seam for scheduled follow-ups and continuations with lease fencing and CAS reply resolution.</summary>
public interface IFollowUpStore
{
    ValueTask<FollowUpRecord> CreateFollowUpAsync(
        FollowUpRecord followUp,
        CancellationToken cancellationToken = default);

    ValueTask<FollowUpRecord?> GetFollowUpAsync(
        TenantId tenantId,
        FollowUpId followUpId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FollowUpRecord>> ClaimDueFollowUpsAsync(
        DateTimeOffset utcNow,
        string leaseOwner,
        TimeSpan leaseDuration,
        int maxBatchSize,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryTransitionFollowUpAsync(
        FollowUpRecord updated,
        FollowUpState expectedState,
        long expectedLeaseFence,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FollowUpRecord>> GetActiveFollowUpsForConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        PrincipalId recipientPrincipalId,
        CancellationToken cancellationToken = default);

    ValueTask<ContinuationRecord> CreateContinuationAsync(
        ContinuationRecord continuation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Cryptographic envelope, device proof-of-possession, key-package, and epoch rekeying provider.
/// Serves as the explicit extension seam for RFC 9420 MLS / OpenMLS native bindings.
/// </summary>
public interface IMessagingCryptoProvider
{
    string ProtocolName { get; }

    ValueTask<bool> VerifyDeviceProofOfPossessionAsync(
        string publicKeyBase64,
        ReadOnlyMemory<byte> challenge,
        ReadOnlyMemory<byte> signature,
        CancellationToken cancellationToken = default);

    ValueTask<string> ComputeKeyFingerprintAsync(
        string publicKeyBase64,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCryptoGroupState> CreateGroupAsync(
        ConversationId conversationId,
        IReadOnlyList<DeviceKeyPackage> initialMemberPackages,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCryptoGroupState> AddDeviceAndRekeyAsync(
        ConversationId conversationId,
        DeviceKeyPackage newMemberPackage,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCryptoGroupState> RemoveDeviceAndRekeyAsync(
        ConversationId conversationId,
        DeviceId removedDeviceId,
        CancellationToken cancellationToken = default);

    ValueTask<EncryptedMessageEnvelope> EncryptAsync(
        ConversationId conversationId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> plaintext,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]> DecryptAsync(
        ConversationId conversationId,
        DeviceId recipientDeviceId,
        EncryptedMessageEnvelope envelope,
        ReadOnlyMemory<byte> associatedData,
        CancellationToken cancellationToken = default);
}

/// <summary>Private authenticated transport client to the OpenConnect IMessaging Python worker.</summary>
public interface IMessagingWorkerClient
{
    ValueTask<MessagingWorkerResult> ExecuteAsync(
        MessagingWorkerCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingWorkerReadiness> CheckReadinessAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Dispatches and deduplicates inbound messaging events from the worker or durable stream.</summary>
public interface IMessagingEventDispatcher
{
    /// <summary>Dispatches an event envelope.</summary>
    /// <returns>
    /// <see langword="true"/> if the event was processed by this call; <see langword="false"/> if it was already
    /// processed. Implementations should record an event as processed only after handling succeeds, and should
    /// propagate failures and cancellation so the event remains redeliverable. Deduplication scope and
    /// durability are implementation specific; handlers should be idempotent.
    /// </returns>
    ValueTask<bool> DispatchAsync(
        MessagingEventEnvelope envelope,
        CancellationToken cancellationToken = default);
}

/// <summary>Domain coordinator interface for conversation lifecycle, human-inclusive invariants, messages, and receipts.</summary>
public interface IMessagingCoordinator
{
    ValueTask<ConversationRecord> CreateConversationAsync(
        MessagingActor creator,
        ConversationSecurityProfile securityProfile,
        IReadOnlyList<MessagingActor> initialParticipants,
        CancellationToken cancellationToken = default);

    ValueTask<ConversationRecord> AddParticipantAsync(
        MessagingActor actor,
        ConversationId conversationId,
        MessagingActor participantToAdd,
        ParticipantRole role,
        CancellationToken cancellationToken = default);

    ValueTask<ParticipantRemovalResult> RemoveParticipantAsync(
        MessagingActor actor,
        ConversationId conversationId,
        ParticipantKind targetKind,
        PrincipalId? targetPrincipalId,
        AgentId? targetAgentId,
        CancellationToken cancellationToken = default);

    ValueTask<MessageCommitResult> CommitMessageAsync(
        MessagingActor sender,
        ConversationId conversationId,
        EncryptedMessageEnvelope envelope,
        string clientOperationId,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    ValueTask<MessageReceiptRecord> RecordReceiptAsync(
        MessagingActor actor,
        ConversationId conversationId,
        MessageId messageId,
        MessageReceiptKind kind,
        string? failureReason = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Domain coordinator interface for desktop QR pairing, device verification, refresh rotation, and revocation.</summary>
public interface IDeviceEnrollmentCoordinator
{
    ValueTask<DeviceEnrollmentCreationResult> InitiateEnrollmentAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId initiatingPrincipalId,
        string issuer,
        string subject,
        Uri baseGatewayUrl,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentRecord> ClaimEnrollmentAsync(
        string plaintextEnrollmentToken,
        DeviceId deviceId,
        string devicePublicKeyBase64,
        string deviceDisplayName,
        DevicePlatform platform,
        ReadOnlyMemory<byte> challengeProofSignature,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentRecord> ApproveEnrollmentAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId approvingPrincipalId,
        EnrollmentId enrollmentId,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentRecord> DenyOrCancelEnrollmentAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        EnrollmentId enrollmentId,
        bool isDenial,
        CancellationToken cancellationToken = default);

    ValueTask<MobileTokenBundle> CompleteEnrollmentAsync(
        TenantId tenantId,
        EnrollmentId enrollmentId,
        DeviceId deviceId,
        ReadOnlyMemory<byte> completionProofSignature,
        string audience,
        CancellationToken cancellationToken = default);

    ValueTask<MobileTokenBundle> RotateRefreshTokenAsync(
        string plaintextRefreshToken,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceRecord> RevokeDeviceAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Domain coordinator interface for voice/media call invitations, CAS acceptance, and E2EE media boundary checks.</summary>
public interface IMessagingCallCoordinator
{
    ValueTask<MessagingCallRecord> CreateCallAsync(
        MessagingActor caller,
        ConversationId conversationId,
        CallMediaSecurityMode mediaSecurityMode,
        bool requireEncryption,
        TimeSpan? ringTimeout = null,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> MarkRingingAsync(
        TenantId tenantId,
        CallId callId,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> AcceptCallAsync(
        MessagingActor acceptor,
        CallId callId,
        SessionId voiceSessionId,
        string admissionTokenHash,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> DeclineCallAsync(
        MessagingActor actor,
        CallId callId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingCallRecord> EndCallAsync(
        MessagingActor actor,
        CallId callId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    ValueTask<int> TerminateCallsForConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>Domain coordinator interface for durable due-time follow-ups, lease fencing, quiet hours, and reply races.</summary>
public interface IFollowUpSchedulerCoordinator
{
    ValueTask<FollowUpRecord> ScheduleFollowUpAsync(
        MessagingActor agentActor,
        ConversationId conversationId,
        PrincipalId recipientPrincipalId,
        DateTimeOffset dueAtUtc,
        int maxAttempts,
        string idempotencyKey,
        string causalEventId,
        TaskId? taskId = null,
        TimeOnly? quietHoursStartUtc = null,
        TimeOnly? quietHoursEndUtc = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FollowUpRecord>> ClaimDueBatchAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        int maxBatchSize,
        CancellationToken cancellationToken = default);

    ValueTask<FollowUpRecord> AuthorizeAndDispatchAsync(
        TenantId tenantId,
        FollowUpId followUpId,
        string leaseOwner,
        long leaseFence,
        Func<FollowUpRecord, CancellationToken, ValueTask<bool>> dispatchCallback,
        CancellationToken cancellationToken = default);

    ValueTask<FollowUpRaceResolution> RecordHumanReplyAsync(
        TenantId tenantId,
        ConversationId conversationId,
        PrincipalId humanPrincipalId,
        string triggeringEventId,
        SessionId? sessionId = null,
        CancellationToken cancellationToken = default);

    ValueTask<FollowUpRecord> CancelFollowUpAsync(
        TenantId tenantId,
        FollowUpId followUpId,
        CancellationToken cancellationToken = default);
}
