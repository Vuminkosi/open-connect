using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Persistence.EFCore.Messaging;

internal static class MessagingPersistenceHelpers
{
    public static readonly string[] ActiveCallStates =
    [
        nameof(CallState.Invited),
        nameof(CallState.Ringing),
        nameof(CallState.Accepted),
        nameof(CallState.Connecting),
        nameof(CallState.Connected)
    ];

    public static readonly string[] ActiveFollowUpStates =
    [
        nameof(FollowUpState.Scheduled),
        nameof(FollowUpState.Claimed),
        nameof(FollowUpState.Authorized),
        nameof(FollowUpState.Dispatched),
        nameof(FollowUpState.Waiting),
        nameof(FollowUpState.Rescheduled)
    ];

    public static DateTime Utc(DateTimeOffset value) => value.UtcDateTime;

    public static DateTime? Utc(DateTimeOffset? value) => value?.UtcDateTime;

    public static DateTimeOffset FromUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? FromUtc(DateTime? value) =>
        value.HasValue ? FromUtc(value.Value) : null;

    public static string Scope(TenantId id) => id.Value.ToString();
    public static string Scope(ConversationId id) => id.Value.ToString();
    public static string Scope(MessageId id) => id.Value.ToString();
    public static string Scope(PrincipalId id) => id.Value.ToString();
    public static string Scope(DeviceId id) => id.Value.ToString();
    public static string Scope(CallId id) => id.Value.ToString();
    public static string Scope(EnrollmentId id) => id.Value.ToString();
    public static string Scope(FollowUpId id) => id.Value.ToString();
    public static string Scope(ContinuationId id) => id.Value.ToString();
    public static string Scope(KeyPackageId id) => id.Value.ToString();
    public static string Scope(RefreshGrantId id) => id.Value.ToString();
    public static string Scope(PushRegistrationId id) => id.Value.ToString();
    public static string Scope(TaskId id) => id.Value.ToString();
    public static string Scope(SessionId id) => id.Value.ToString();
    public static string Scope(AgentId id) => id.Value;

    public static string RequireApplicationId(string applicationId)
    {
        if (string.IsNullOrWhiteSpace(applicationId))
            throw new MessagingDomainException("invalid_request", "ApplicationId is required.", 400);
        return applicationId;
    }

    public static string BuildParticipantKey(ParticipantKind kind, PrincipalId? principalId, AgentId? agentId)
    {
        return kind switch
        {
            ParticipantKind.Human when principalId.HasValue => $"human:{principalId.Value.Value}",
            ParticipantKind.Agent when agentId.HasValue && !string.IsNullOrWhiteSpace(agentId.Value.Value) => $"agent:{agentId.Value.Value}",
            _ => throw new MessagingDomainException("invalid_request", $"Participant of kind '{kind}' requires a matching identifier.", 400)
        };
    }

    public static int ReceiptRank(MessageReceiptKind kind) => kind switch
    {
        MessageReceiptKind.Failed => 0,
        MessageReceiptKind.Queued => 1,
        MessageReceiptKind.Committed => 2,
        MessageReceiptKind.Delivered => 3,
        MessageReceiptKind.Read => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported receipt kind.")
    };

    public static string? FormatTimeOnly(TimeOnly? value) =>
        value?.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static TimeOnly? ParseTimeOnly(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : TimeOnly.ParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture);

    public static string SerializePrincipalIds(IReadOnlyList<PrincipalId> ids) =>
        JsonSerializer.Serialize(ids.Select(static x => x.Value.ToString()).ToArray());

    public static IReadOnlyList<PrincipalId> DeserializePrincipalIds(string json)
    {
        var raw = JsonSerializer.Deserialize<string[]>(json) ?? [];
        return raw.Select(static s => new PrincipalId(Ulid.Parse(s))).ToArray();
    }

    public static string BuildDurableSubject(string tenantId, string conversationId) =>
        $"oc.v1.messaging.durable.{tenantId}.{conversationId}";

    public static async ValueTask<IDbContextTransaction?> BeginIfNeededAsync(
        DbContext context,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
            return null;
        return await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    public static ConversationRecord ToRecord(MessagingConversationEntity entity)
    {
        var conversationId = new ConversationId(Ulid.Parse(entity.ConversationId));
        var participants = entity.Participants
            .OrderBy(p => p.JoinVersion)
            .ThenBy(p => p.ParticipantKey, StringComparer.Ordinal)
            .Select(p => new ConversationParticipant(
                conversationId,
                Enum.Parse<ParticipantKind>(p.Kind),
                p.PrincipalId is null ? null : new PrincipalId(Ulid.Parse(p.PrincipalId)),
                p.AgentId is null ? null : new AgentId(p.AgentId),
                Enum.Parse<ParticipantRole>(p.Role),
                Enum.Parse<ParticipantStatus>(p.Status),
                p.JoinVersion,
                p.LeaveVersion,
                FromUtc(p.JoinedAtUtc)))
            .ToArray();

        return new ConversationRecord(
            new TenantId(Ulid.Parse(entity.TenantId)),
            entity.ApplicationId,
            conversationId,
            Enum.Parse<ConversationSecurityProfile>(entity.SecurityProfile),
            Enum.Parse<ConversationState>(entity.State),
            entity.MembershipVersion,
            entity.CryptoEpoch,
            entity.NextSequence,
            FromUtc(entity.CreatedAtUtc),
            participants);
    }

    public static MessageRecord ToRecord(MessagingMessageEntity entity) =>
        new(
            new TenantId(Ulid.Parse(entity.TenantId)),
            new ConversationId(Ulid.Parse(entity.ConversationId)),
            new MessageId(Ulid.Parse(entity.MessageId)),
            entity.Sequence,
            Enum.Parse<ParticipantKind>(entity.SenderKind),
            entity.SenderPrincipalId is null ? null : new PrincipalId(Ulid.Parse(entity.SenderPrincipalId)),
            entity.SenderAgentId is null ? null : new AgentId(entity.SenderAgentId),
            entity.SenderDeviceId is null ? null : new DeviceId(Ulid.Parse(entity.SenderDeviceId)),
            new EncryptedMessageEnvelope(
                entity.Protocol,
                entity.CryptoEpoch,
                entity.CiphertextBase64,
                entity.NonceBase64,
                entity.AuthTagBase64,
                entity.AssociatedDataBase64,
                entity.SenderKeyId),
            entity.ClientOperationId,
            entity.PayloadDigestSha256,
            FromUtc(entity.CreatedAtUtc),
            entity.CorrelationId);

    public static MessageReceiptRecord ToRecord(MessagingReceiptEntity entity) =>
        new(
            new TenantId(Ulid.Parse(entity.TenantId)),
            new ConversationId(Ulid.Parse(entity.ConversationId)),
            new MessageId(Ulid.Parse(entity.MessageId)),
            new PrincipalId(Ulid.Parse(entity.PrincipalId)),
            new DeviceId(Ulid.Parse(entity.DeviceId)),
            Enum.Parse<MessageReceiptKind>(entity.Kind),
            entity.Sequence,
            FromUtc(entity.RecordedAtUtc),
            entity.FailureReason);
}
