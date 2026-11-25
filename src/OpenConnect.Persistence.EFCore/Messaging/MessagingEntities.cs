namespace OpenConnect.Persistence.EFCore.Messaging;

public sealed class MessagingConversationEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string SecurityProfile { get; set; } = null!;
    public string State { get; set; } = null!;
    public long MembershipVersion { get; set; }
    public long CryptoEpoch { get; set; }
    public long NextSequence { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<MessagingParticipantEntity> Participants { get; set; } = new List<MessagingParticipantEntity>();
}

public sealed class MessagingParticipantEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string? PrincipalId { get; set; }
    public string? AgentId { get; set; }
    public string ParticipantKey { get; set; } = null!;
    public string Role { get; set; } = null!;
    public string Status { get; set; } = null!;
    public long JoinVersion { get; set; }
    public long? LeaveVersion { get; set; }
    public DateTime JoinedAtUtc { get; set; }

    public MessagingConversationEntity Conversation { get; set; } = null!;
}

public sealed class MessagingDeviceEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string DeviceId { get; set; } = null!;
    public string PrincipalId { get; set; } = null!;
    public string Platform { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string PublicKeyBase64 { get; set; } = null!;
    public string KeyFingerprint { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
}

public sealed class MessagingMessageEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string MessageId { get; set; } = null!;
    public long Sequence { get; set; }
    public string SenderKind { get; set; } = null!;
    public string? SenderPrincipalId { get; set; }
    public string? SenderAgentId { get; set; }
    public string? SenderDeviceId { get; set; }
    public string SenderKey { get; set; } = null!;
    public string Protocol { get; set; } = null!;
    public long CryptoEpoch { get; set; }
    public string CiphertextBase64 { get; set; } = null!;
    public string NonceBase64 { get; set; } = null!;
    public string AuthTagBase64 { get; set; } = null!;
    public string? AssociatedDataBase64 { get; set; }
    public string? SenderKeyId { get; set; }
    public string ClientOperationId { get; set; } = null!;
    public string PayloadDigestSha256 { get; set; } = null!;
    public string? CorrelationId { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public MessagingConversationEntity Conversation { get; set; } = null!;
}

public sealed class MessagingReceiptEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string MessageId { get; set; } = null!;
    public string PrincipalId { get; set; } = null!;
    public string DeviceId { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public int Rank { get; set; }
    public long Sequence { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    public string? FailureReason { get; set; }

    public MessagingMessageEntity Message { get; set; } = null!;
}

public sealed class MessagingKeyPackageEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string KeyPackageId { get; set; } = null!;
    public string? OwnerPrincipalId { get; set; }
    public string? OwnerAgentId { get; set; }
    public string DeviceId { get; set; } = null!;
    public string Protocol { get; set; } = null!;
    public string PublicPackageBase64 { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool Consumed { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}

public sealed class MessagingAttachmentEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string AttachmentId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string ObjectKey { get; set; } = null!;
    public string ObjectVersion { get; set; } = null!;
    public long EncryptedSizeBytes { get; set; }
    public string EncryptedDigestSha256 { get; set; } = null!;
    public string State { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    public MessagingConversationEntity Conversation { get; set; } = null!;
}

public sealed class MessagingCallEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string CallId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string CallerKind { get; set; } = null!;
    public string? CallerPrincipalId { get; set; }
    public string? CallerAgentId { get; set; }
    public string AllowedHumanRecipientsJson { get; set; } = null!;
    public string State { get; set; } = null!;
    public string MediaSecurityMode { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? AcceptedDeviceId { get; set; }
    public string? AcceptedPrincipalId { get; set; }
    public string? VoiceSessionId { get; set; }
    public string? AdmissionTokenHash { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }
    public long Version { get; set; }

    public MessagingConversationEntity Conversation { get; set; } = null!;
}

public sealed class MessagingFollowUpEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string FollowUpId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string? TaskId { get; set; }
    public string AgentId { get; set; } = null!;
    public string RecipientPrincipalId { get; set; } = null!;
    public DateTime DueAtUtc { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; }
    public string State { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string CausalEventId { get; set; } = null!;
    public string? LeaseOwner { get; set; }
    public long LeaseFence { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? QuietHoursStartUtc { get; set; }
    public string? QuietHoursEndUtc { get; set; }
    public string? TimeZoneId { get; set; }
    public DateTime? LastDispatchedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public MessagingConversationEntity Conversation { get; set; } = null!;
}

public sealed class MessagingContinuationEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string ContinuationId { get; set; } = null!;
    public string ConversationId { get; set; } = null!;
    public string TriggeringEventId { get; set; } = null!;
    public string AgentId { get; set; } = null!;
    public string RecipientPrincipalId { get; set; } = null!;
    public string? TaskId { get; set; }
    public string? SessionId { get; set; }
    public string Status { get; set; } = null!;
    public string ExecutionIdempotencyKey { get; set; } = null!;
    public string? LeaseOwner { get; set; }
    public long LeaseFence { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public MessagingConversationEntity Conversation { get; set; } = null!;
}

public sealed class MessagingEnrollmentEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string EnrollmentId { get; set; } = null!;
    public string InitiatingPrincipalId { get; set; } = null!;
    public string Issuer { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string TokenHashSha256 { get; set; } = null!;
    public string VerificationCode { get; set; } = null!;
    public string State { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? ClaimedDeviceId { get; set; }
    public string? ClaimedDevicePublicKeyBase64 { get; set; }
    public string? ClaimedDeviceName { get; set; }
    public string? ClaimedPlatform { get; set; }
    public string? ChallengeNonceBase64 { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class MessagingRefreshGrantEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string GrantId { get; set; } = null!;
    public string FamilyId { get; set; } = null!;
    public string DeviceId { get; set; } = null!;
    public string PrincipalId { get; set; } = null!;
    public string Issuer { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Audience { get; set; } = null!;
    public string TokenHashSha256 { get; set; } = null!;
    public string Status { get; set; } = null!;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RotatedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public long Version { get; set; }
}

public sealed class MessagingPushRegistrationEntity
{
    public string TenantId { get; set; } = null!;
    public string ApplicationId { get; set; } = null!;
    public string RegistrationId { get; set; } = null!;
    public string PrincipalId { get; set; } = null!;
    public string DeviceId { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string ProtectedTokenHandle { get; set; } = null!;
    public bool ConsentGranted { get; set; }
    public string Status { get; set; } = null!;
    public DateTime UpdatedAtUtc { get; set; }
}
