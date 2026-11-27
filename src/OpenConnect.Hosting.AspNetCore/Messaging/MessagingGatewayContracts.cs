using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Hosting.AspNetCore.Messaging;

/// <summary>Result of a paginated or cursor-based messaging query.</summary>
public sealed record MessagingEventPage(
    IReadOnlyList<MessagingEventEnvelope> Events,
    string? NextCursor,
    bool HasMore);

/// <summary>Short-lived single-use WebSocket ticket bound to a verified principal, device, and optional origin.</summary>
public sealed record MessagingWebSocketTicket(
    string Ticket,
    TenantId TenantId,
    string ApplicationId,
    PrincipalId PrincipalId,
    DeviceId? DeviceId,
    string? AllowedOrigin,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Short-lived single-use voice admission credential issued upon call acceptance.</summary>
public sealed record MessagingVoiceAdmission(
    SessionId VoiceSessionId,
    string AdmissionToken,
    string AdmissionTokenHash,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Encrypted attachment download payload returned by <see cref="IMessagingAttachmentStore"/>.</summary>
public sealed record MessagingAttachmentDownload(
    AttachmentRecord Metadata,
    ReadOnlyMemory<byte> EncryptedBytes);

/// <summary>Durable store seam for publishing and consuming single-use device key packages.</summary>
public interface IMessagingKeyPackageStore
{
    ValueTask<DeviceKeyPackage> PublishAsync(
        DeviceKeyPackage keyPackage,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceKeyPackage?> ConsumeOneAsync(
        TenantId tenantId,
        PrincipalId? ownerPrincipalId,
        AgentId? ownerAgentId,
        DeviceId? deviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable store seam for encrypted attachment reservation, commit, and authorized download.</summary>
public interface IMessagingAttachmentStore
{
    ValueTask<AttachmentRecord> InitUploadAsync(
        TenantId tenantId,
        ConversationId conversationId,
        long encryptedSizeBytes,
        string encryptedDigestSha256,
        TimeSpan expiry,
        CancellationToken cancellationToken = default);

    ValueTask<AttachmentRecord> CompleteUploadAsync(
        TenantId tenantId,
        AttachmentId attachmentId,
        ReadOnlyMemory<byte> encryptedBytes,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingAttachmentDownload?> DownloadAsync(
        TenantId tenantId,
        AttachmentId attachmentId,
        CancellationToken cancellationToken = default);
}

/// <summary>Read/replay seam for conversations, message history, and user/device-scoped event replay.</summary>
public interface IMessagingEventReplayStore
{
    ValueTask<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        TenantId tenantId,
        PrincipalId principalId,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        TenantId tenantId,
        ConversationId conversationId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MessagingEventEnvelope>> ReplayEventsAsync(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId? deviceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Store seam for device push notification registrations (stores protected token handles only).</summary>
public interface IMessagingPushRegistrationStore
{
    ValueTask<PushRegistrationRecord> RegisterAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId deviceId,
        PushProviderKind provider,
        string protectedTokenHandle,
        bool consentGranted,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RevokeAsync(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId deviceId,
        PushProviderKind? provider = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Issues and atomically consumes short-lived single-use WebSocket authentication tickets.</summary>
public interface IMessagingWebSocketTicketService
{
    ValueTask<MessagingWebSocketTicket> IssueAsync(
        TenantId tenantId,
        string applicationId,
        PrincipalId principalId,
        DeviceId? deviceId,
        string? allowedOrigin,
        CancellationToken cancellationToken = default);

    ValueTask<MessagingWebSocketTicket?> TryConsumeAsync(
        string ticket,
        string? requestOrigin,
        CancellationToken cancellationToken = default);
}

/// <summary>Manages user/device-scoped realtime WebSocket connections with bounded queues and immediate revocation teardown.</summary>
public interface IMessagingRealtimeHub
{
    int ActiveConnectionCount { get; }

    ValueTask PublishToPrincipalAsync(
        TenantId tenantId,
        PrincipalId principalId,
        MessagingEventEnvelope envelope,
        CancellationToken cancellationToken = default);

    ValueTask<int> CloseDeviceConnectionsAsync(
        TenantId tenantId,
        DeviceId deviceId,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>Wraps existing duplex voice session admission for accepted messaging calls.</summary>
public interface IMessagingVoiceAdmissionBroker
{
    ValueTask<MessagingVoiceAdmission> IssueAdmissionAsync(
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId deviceId,
        CallId callId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dispatches a claimed, freshly authorized due follow-up.</summary>
public interface IMessagingFollowUpDispatcher
{
    ValueTask<bool> DispatchAsync(
        FollowUpRecord followUp,
        CancellationToken cancellationToken = default);
}
