namespace OpenConnect.Components.Contracts.Messaging;

/// <summary>Connection and lifecycle state of the user-scoped messaging stream.</summary>
public enum MessagingConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Offline = 4,
    Revoked = 5,
    Error = 6
}

/// <summary>UI representation of the immutable conversation security profile.</summary>
public enum MessagingSecurityProfileKind
{
    PrivateE2EE = 0,
    AgentE2EE = 1,
    Governed = 2
}

/// <summary>UI representation of conversation lifecycle state.</summary>
public enum MessagingConversationLifecycleState
{
    Active = 0,
    Closed = 1,
    Archived = 2
}

/// <summary>UI representation of monotonic message delivery receipt states.</summary>
public enum MessagingReceiptStatus
{
    Queued = 0,
    Committed = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4
}

/// <summary>UI representation of call media encryption boundary.</summary>
public enum MessagingCallSecurityKind
{
    TransportOnly = 0,
    EndpointE2EE = 1
}

/// <summary>UI representation of desktop-to-mobile enrollment state.</summary>
public enum MessagingPairingState
{
    Pending = 0,
    Claimed = 1,
    Approved = 2,
    Consumed = 3,
    Expired = 4,
    Cancelled = 5,
    Denied = 6
}

/// <summary>Opt-in UI options for embedding IMessaging and Linked Devices in host applications or MiniComposer.</summary>
public sealed record MessagingUiOptions
{
    public bool EnableMessaging { get; init; }
    public bool EnableLinkedDevices { get; init; }
    public bool ShowUnreadBadgeOnFab { get; init; } = true;
    public bool ShowIncomingToasts { get; init; } = true;
}

/// <summary>UI projection of a conversation in the inbox list.</summary>
public sealed record MessagingConversationSummary(
    string ConversationId,
    string Title,
    MessagingSecurityProfileKind SecurityProfile,
    MessagingConversationLifecycleState State,
    int UnreadCount,
    bool IncludesOperationalAgent,
    string? LastMessagePreview,
    DateTimeOffset? LastMessageAtUtc,
    MessagingReceiptStatus? LastReceiptKind = null,
    string? ActiveTypingLabel = null);

/// <summary>UI projection of a single message rendered inside a conversation thread.</summary>
public sealed record MessagingThreadMessage(
    string MessageId,
    long Sequence,
    string SenderDisplayName,
    bool IsAgentSender,
    bool IsOwnMessage,
    string BodyText,
    bool IsDecrypted,
    long CryptoEpoch,
    DateTimeOffset SentAtUtc,
    MessagingReceiptStatus ReceiptKind,
    string? AttachmentName = null,
    bool IsVoiceNote = false,
    string? CorrelationId = null,
    bool IsIncompleteGeneration = false);

/// <summary>UI projection of an enrolled device shown in Linked Devices.</summary>
public sealed record MessagingLinkedDeviceSummary(
    string DeviceId,
    string DisplayName,
    string Platform,
    string KeyFingerprint,
    string Status,
    bool IsCurrentSession,
    bool IsRevoked,
    DateTimeOffset LastSeenAtUtc);

/// <summary>UI projection of an active desktop-to-mobile QR pairing session.</summary>
public sealed record MessagingPairingSession(
    string EnrollmentId,
    string CompanyDisplayName,
    string ApplicationDisplayName,
    string QrPayloadJson,
    string VerificationCode,
    MessagingPairingState State,
    DateTimeOffset ExpiresAtUtc,
    string? ClaimedDeviceName = null,
    string? ClaimedDevicePlatform = null);

/// <summary>UI projection of an incoming voice/media call invitation requiring explicit user consent.</summary>
public sealed record MessagingCallInvitation(
    string CallId,
    string ConversationId,
    string CallerDisplayName,
    bool IsAgentCaller,
    string ConversationTitle,
    MessagingCallSecurityKind MediaSecurityMode,
    DateTimeOffset ExpiresAtUtc);

/// <summary>UI projection of an active connected or connecting voice/media call.</summary>
public sealed record MessagingActiveCall(
    string CallId,
    string ConversationId,
    string CounterpartyDisplayName,
    string StateLabel,
    MessagingCallSecurityKind MediaSecurityMode,
    bool IsMuted,
    bool IsCounterpartySpeaking,
    DateTimeOffset StartedAtUtc);

/// <summary>UI projection of a typed action approval request bound to an operational agent workflow.</summary>
public sealed record MessagingApprovalRequest(
    string ApprovalRequestId,
    string ConversationId,
    string ActionName,
    string ActionSummary,
    string RequestedByDisplayName,
    DateTimeOffset ExpiresAtUtc,
    string? ResourceScope = null,
    bool IsResolved = false,
    bool? Approved = null);

/// <summary>UI projection of a non-intrusive incoming message toast.</summary>
public sealed record MessagingToastNotification(
    string NotificationId,
    string ConversationId,
    string SenderDisplayName,
    string SummaryText,
    MessagingSecurityProfileKind SecurityProfile,
    DateTimeOffset OccurredAtUtc);
