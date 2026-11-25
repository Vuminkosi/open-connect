using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Messaging;
using OpenConnect.Hosting.AspNetCore.Messaging;
using OpenConnect.Samples.Blazor.Diagnostics;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor.Messaging;

/// <summary>
/// Dispatches claimed, freshly authorized durable follow-ups as real encrypted E2EE messages
/// and publishes realtime wake notifications.
/// </summary>
public sealed class TodoMessagingFollowUpDispatcher(
    IMessagingCoordinator messagingCoordinator,
    IMessagingCryptoProvider cryptoProvider,
    TodoAcceptanceDiagnosticsService diagnostics,
    IMessagingRealtimeHub? realtimeHub = null) : IMessagingFollowUpDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DeviceId FollowUpAgentDeviceId = new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FD0"));

    public async ValueTask<bool> DispatchAsync(FollowUpRecord followUp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(followUp);

        try
        {
            var pkg = MessagingCryptoEnvelopeService.CreateKeyPackage(
                followUp.TenantId,
                FollowUpAgentDeviceId,
                ownerAgentId: followUp.AgentId);
            await cryptoProvider.CreateGroupAsync(followUp.ConversationId, [pkg], cancellationToken).ConfigureAwait(false);
        }
        catch (MessagingDomainException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.Conflict, StringComparison.Ordinal))
        {
            // Group already exists.
        }

        var agentActor = new MessagingActor(
            followUp.TenantId,
            followUp.ApplicationId,
            ParticipantKind.Agent,
            null,
            followUp.AgentId,
            FollowUpAgentDeviceId);

        var payload = JsonSerializer.Serialize(new
        {
            kind = "durable_followup_reminder",
            followUpId = followUp.FollowUpId.Value.ToString(),
            attempt = followUp.Attempt,
            maxAttempts = followUp.MaxAttempts,
            causalEventId = followUp.CausalEventId,
            message = $"Follow-up reminder #{followUp.Attempt}/{followUp.MaxAttempts} for pending overdue task."
        }, JsonOptions);

        var aad = Encoding.UTF8.GetBytes($"{followUp.TenantId.Value}|{followUp.ConversationId.Value}|{followUp.FollowUpId.Value}");
        var envelope = await cryptoProvider.EncryptAsync(
            followUp.ConversationId,
            FollowUpAgentDeviceId,
            Encoding.UTF8.GetBytes(payload),
            aad,
            cancellationToken).ConfigureAwait(false);

        var committed = await messagingCoordinator.CommitMessageAsync(
            agentActor,
            followUp.ConversationId,
            envelope,
            clientOperationId: $"followup:{followUp.FollowUpId.Value}:attempt:{followUp.Attempt}",
            correlationId: followUp.CausalEventId,
            cancellationToken).ConfigureAwait(false);

        if (realtimeHub is not null)
        {
            var wireEvent = new MessagingEventEnvelope(
                SchemaVersion: 1,
                EventId: $"msg-{committed.Message.MessageId.Value}",
                Type: "messaging.message.committed",
                TenantId: followUp.TenantId,
                ConversationId: followUp.ConversationId,
                Sequence: committed.Message.Sequence,
                Cursor: committed.Message.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OccurredAt: committed.Message.CreatedAt,
                PayloadJson: JsonSerializer.Serialize(committed.Message, JsonOptions),
                CorrelationId: followUp.CausalEventId);
            await realtimeHub.PublishToPrincipalAsync(followUp.TenantId, followUp.RecipientPrincipalId, wireEvent, cancellationToken).ConfigureAwait(false);
        }

        diagnostics.RecordPushAttempt(
            followUp.TenantId.Value.ToString(),
            followUp.RecipientPrincipalId.Value.ToString(),
            null,
            "wake-only",
            diagnostics.IsWorkerOutageInjected ? "degraded_queued" : "dispatched",
            $"Follow-up #{followUp.Attempt} wake for conversation {followUp.ConversationId.Value} (no plaintext).");

        return true;
    }
}
