using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Tools.Security;

namespace OpenConnect.Tools.Messaging;

/// <summary>
/// Registers and executes the 11 canonical host-brokered MAF operational messaging tools through
/// <see cref="ToolCatalog"/>, <see cref="ToolBroker"/>, and <see cref="ToolAuthorizationService"/>.
/// Enforces trusted execution context, per-agent permissions, human-inclusive invariants, bounded
/// causal execution depth, typed signed approval descriptors, and zero exposure of secrets/keys/URLs.
/// </summary>
public sealed class MessagingToolSuite
{
    private readonly MessagingToolPolicyOptions _options;
    private readonly IMessagingRecipientResolver _recipientResolver;
    private readonly IMessagingAuthorizer _authorizer;
    private readonly IMessagingConversationStore _conversationStore;
    private readonly IMessagingCoordinator _messagingCoordinator;
    private readonly IMessagingCryptoProvider? _cryptoProvider;
    private readonly IMessagingCallCoordinator? _callCoordinator;
    private readonly IFollowUpSchedulerCoordinator? _followUpCoordinator;
    private readonly IFollowUpStore? _followUpStore;
    private readonly IMessagingToolHistoryProvider? _historyProvider;
    private readonly IMessagingApprovalStore? _approvalStore;
    private readonly IMessagingEscalationHandler? _escalationHandler;
    private readonly MessagingApprovalDescriptorSigner _approvalSigner;
    private readonly TimeProvider _timeProvider;

    public MessagingToolSuite(
        MessagingToolPolicyOptions options,
        IMessagingRecipientResolver recipientResolver,
        IMessagingAuthorizer authorizer,
        IMessagingConversationStore conversationStore,
        IMessagingCoordinator messagingCoordinator,
        IMessagingCryptoProvider? cryptoProvider = null,
        IMessagingCallCoordinator? callCoordinator = null,
        IFollowUpSchedulerCoordinator? followUpCoordinator = null,
        IFollowUpStore? followUpStore = null,
        IMessagingToolHistoryProvider? historyProvider = null,
        IMessagingApprovalStore? approvalStore = null,
        IMessagingEscalationHandler? escalationHandler = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _recipientResolver = recipientResolver ?? throw new ArgumentNullException(nameof(recipientResolver));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _messagingCoordinator = messagingCoordinator ?? throw new ArgumentNullException(nameof(messagingCoordinator));
        _cryptoProvider = cryptoProvider;
        _callCoordinator = callCoordinator;
        _followUpCoordinator = followUpCoordinator;
        _followUpStore = followUpStore;
        _historyProvider = historyProvider;
        _approvalStore = approvalStore;
        _escalationHandler = escalationHandler;
        _approvalSigner = new MessagingApprovalDescriptorSigner(options.ApprovalSigningKey);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns static metadata for all 11 canonical operational messaging tools.</summary>
    public static IReadOnlyList<MessagingToolMetadata> GetAllToolMetadata() =>
    [
        new(
            new ToolId(MessagingToolIds.ResolveRecipient),
            "Messaging Resolve Recipient",
            "Resolves an authorized human recipient within the current tenant and application (default deny; never resolves arbitrary external addresses).",
            """{"type":"object","properties":{"principal_id":{"type":"string"}},"required":["principal_id"],"additionalProperties":false}""",
            MessagingPermission.Discover,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.Send),
            "Messaging Send",
            "Encrypts and sends a message to a human-inclusive conversation after fresh authorization.",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"text":{"type":"string"},"client_operation_id":{"type":"string"}},"required":["conversation_id","text","client_operation_id"],"additionalProperties":false}""",
            MessagingPermission.Send,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.RequestResponse),
            "Messaging Request Response",
            "Sends a prompt to a human participant in a conversation and optionally schedules a bounded follow-up reminder.",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"recipient_principal_id":{"type":"string"},"text":{"type":"string"},"client_operation_id":{"type":"string"},"followup_after_seconds":{"type":"integer"}},"required":["conversation_id","recipient_principal_id","text","client_operation_id"],"additionalProperties":false}""",
            MessagingPermission.Send,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.GetStatus),
            "Messaging Get Status",
            "Returns conversation state, membership version, active human count, and optional message receipt status.",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"message_id":{"type":"string"}},"required":["conversation_id"],"additionalProperties":false}""",
            MessagingPermission.Receive,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.GetHistory),
            "Messaging Get History",
            "Retrieves bounded participant-scoped decrypted conversation history at the authorized agent endpoint.",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"limit":{"type":"integer"}},"required":["conversation_id"],"additionalProperties":false}""",
            MessagingPermission.Receive,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.ScheduleFollowUp),
            "Messaging Schedule Follow-Up",
            "Schedules a durable due-time operational follow-up for a verified human recipient.",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"recipient_principal_id":{"type":"string"},"due_at_utc":{"type":"string"},"max_attempts":{"type":"integer"},"idempotency_key":{"type":"string"}},"required":["conversation_id","recipient_principal_id","due_at_utc","idempotency_key"],"additionalProperties":false}""",
            MessagingPermission.FollowUp,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.CancelFollowUp),
            "Messaging Cancel Follow-Up",
            "Cancels a previously scheduled durable follow-up.",
            """{"type":"object","properties":{"followup_id":{"type":"string"}},"required":["followup_id"],"additionalProperties":false}""",
            MessagingPermission.FollowUp,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.SendVoiceNote),
            "Messaging Send Voice Note",
            "Sends an encrypted voice note reference to a human-inclusive conversation.",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"attachment_id":{"type":"string"},"duration_seconds":{"type":"integer"},"client_operation_id":{"type":"string"}},"required":["conversation_id","attachment_id","duration_seconds","client_operation_id"],"additionalProperties":false}""",
            MessagingPermission.Attachment,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.StartCall),
            "Messaging Start Call",
            "Initiates an authorized call invitation to human participants in a conversation (never auto-answers; enforces E2EE media gate).",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"media_security_mode":{"type":"string"},"ring_timeout_seconds":{"type":"integer"}},"required":["conversation_id"],"additionalProperties":false}""",
            MessagingPermission.Call,
            RequiresApproval: true),
        new(
            new ToolId(MessagingToolIds.RequestApproval),
            "Messaging Request Approval",
            "Creates a typed, HMAC-signed, time-bounded approval descriptor for an explicit human decision (never infers approval from free text).",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"target_human_principal_id":{"type":"string"},"action_type":{"type":"string"},"action_summary":{"type":"string"},"parameters_json":{"type":"string"},"expires_in_seconds":{"type":"integer"}},"required":["conversation_id","target_human_principal_id","action_type","action_summary"],"additionalProperties":false}""",
            MessagingPermission.Approval,
            RequiresApproval: false),
        new(
            new ToolId(MessagingToolIds.Escalate),
            "Messaging Escalate",
            "Escalates an active operational thread to a verified human supervisor.",
            """{"type":"object","properties":{"conversation_id":{"type":"string"},"target_human_principal_id":{"type":"string"},"reason":{"type":"string"},"severity":{"type":"string"}},"required":["conversation_id","target_human_principal_id","reason"],"additionalProperties":false}""",
            MessagingPermission.Send,
            RequiresApproval: false)
    ];

    /// <summary>Registers all 11 messaging tools in the supplied <see cref="ToolCatalog"/> for the specified owner agent.</summary>
    public void RegisterAll(ToolCatalog catalog, AgentId ownerAgentId, IEnumerable<AgentId>? allowedAgents = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var agents = allowedAgents?.ToArray() ?? [ownerAgentId];

        foreach (var meta in GetAllToolMetadata())
        {
            Func<JsonElement, CancellationToken, ValueTask<JsonElement>> handler = meta.ToolId.Value switch
            {
                MessagingToolIds.ResolveRecipient => HandleResolveRecipientAsync,
                MessagingToolIds.Send => HandleSendAsync,
                MessagingToolIds.RequestResponse => HandleRequestResponseAsync,
                MessagingToolIds.GetStatus => HandleGetStatusAsync,
                MessagingToolIds.GetHistory => HandleGetHistoryAsync,
                MessagingToolIds.ScheduleFollowUp => HandleScheduleFollowUpAsync,
                MessagingToolIds.CancelFollowUp => HandleCancelFollowUpAsync,
                MessagingToolIds.SendVoiceNote => HandleSendVoiceNoteAsync,
                MessagingToolIds.StartCall => HandleStartCallAsync,
                MessagingToolIds.RequestApproval => HandleRequestApprovalAsync,
                MessagingToolIds.Escalate => HandleEscalateAsync,
                _ => throw new InvalidOperationException($"Unsupported messaging tool '{meta.ToolId.Value}'.")
            };

            var descriptor = new ToolDescriptor(meta.ToolId, meta.Name, ownerAgentId);
            catalog.Register(new ToolRegistration(descriptor, meta.JsonSchema, handler, meta.RequiresApproval), agents);
        }
    }

    private async ValueTask<JsonElement> HandleResolveRecipientAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Discover);
        var principalId = RequireUlidPrincipal(arguments, "principal_id");

        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Discover, null, null, cancellationToken).ConfigureAwait(false);

        var resolved = await _recipientResolver.ResolveHumanAsync(ctx.TenantId, ctx.ApplicationId, principalId, cancellationToken).ConfigureAwait(false);
        if (resolved is null || resolved.Kind != ParticipantKind.Human || resolved.TenantId != ctx.TenantId)
        {
            return JsonSerializer.SerializeToElement(new
            {
                resolved = false,
                error_code = ErrorCodes.Unauthorized
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            resolved = true,
            principal_id = resolved.PrincipalId!.Value.Value.ToString(),
            participant_kind = "human"
        });
    }

    private async ValueTask<JsonElement> HandleSendAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Send);
        var conversationId = RequireConversationId(arguments, "conversation_id");
        var text = RequireBoundedString(arguments, "text", _options.MaxMessageLength);
        var clientOperationId = RequireBoundedString(arguments, "client_operation_id", 128);

        var conversation = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Send, conversationId, null, cancellationToken).ConfigureAwait(false);

        var envelope = await EncryptAgentPayloadAsync(conversation, ctx, Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        var commit = await _messagingCoordinator.CommitMessageAsync(actor, conversationId, envelope, clientOperationId, ctx.CausalEventId, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(new
        {
            status = "committed",
            message_id = commit.Message.MessageId.Value.ToString(),
            conversation_id = commit.Message.ConversationId.Value.ToString(),
            sequence = commit.Message.Sequence,
            deduplicated = commit.WasDeduplicated,
            crypto_epoch = commit.Message.Envelope.CryptoEpoch
        });
    }

    private async ValueTask<JsonElement> HandleRequestResponseAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Send);
        var conversationId = RequireConversationId(arguments, "conversation_id");
        var recipientPrincipalId = RequireUlidPrincipal(arguments, "recipient_principal_id");
        var text = RequireBoundedString(arguments, "text", _options.MaxMessageLength);
        var clientOperationId = RequireBoundedString(arguments, "client_operation_id", 128);

        var conversation = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        EnsureActiveHumanMember(conversation, recipientPrincipalId);

        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Send, conversationId, null, cancellationToken).ConfigureAwait(false);

        var envelope = await EncryptAgentPayloadAsync(conversation, ctx, Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        var commit = await _messagingCoordinator.CommitMessageAsync(actor, conversationId, envelope, clientOperationId, ctx.CausalEventId, cancellationToken).ConfigureAwait(false);

        string? followUpId = null;
        if (arguments.TryGetProperty("followup_after_seconds", out var delayProp) && delayProp.TryGetInt64(out var delaySeconds) && delaySeconds > 0)
        {
            if (!_options.HasPermission(ctx.AgentId, MessagingPermission.FollowUp) || _followUpCoordinator is null)
                throw new ToolAuthorizationException(ErrorCodes.ToolNotAllowed);

            await EnsureAuthorizedAsync(actor, MessagingPermission.FollowUp, conversationId, null, cancellationToken).ConfigureAwait(false);
            var dueAt = _timeProvider.GetUtcNow().AddSeconds(Math.Min(delaySeconds, 7 * 24 * 3600));
            var followUp = await _followUpCoordinator.ScheduleFollowUpAsync(
                actor,
                conversationId,
                recipientPrincipalId,
                dueAt,
                _options.MaxFollowUpAttempts,
                $"req-{clientOperationId}",
                ctx.CausalEventId ?? commit.Message.MessageId.Value.ToString(),
                ctx.TaskId,
                null,
                null,
                cancellationToken).ConfigureAwait(false);
            followUpId = followUp.FollowUpId.Value.ToString();
        }

        return JsonSerializer.SerializeToElement(new
        {
            status = "awaiting_human_response",
            message_id = commit.Message.MessageId.Value.ToString(),
            conversation_id = conversationId.Value.ToString(),
            sequence = commit.Message.Sequence,
            followup_id = followUpId
        });
    }

    private async ValueTask<JsonElement> HandleGetStatusAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Receive);
        var conversationId = RequireConversationId(arguments, "conversation_id");

        var conversation = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Receive, conversationId, null, cancellationToken).ConfigureAwait(false);

        var activeHumans = conversation.Participants.Count(p => p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active);
        string? messageStatus = null;
        if (arguments.TryGetProperty("message_id", out var msgProp) && msgProp.ValueKind == JsonValueKind.String &&
            TryParseUlid(msgProp.GetString(), out var msgUlid))
        {
            var msg = await _conversationStore.GetMessageAsync(ctx.TenantId, conversationId, new MessageId(msgUlid), cancellationToken).ConfigureAwait(false);
            messageStatus = msg is not null ? "committed" : "not_found";
        }

        return JsonSerializer.SerializeToElement(new
        {
            conversation_id = conversation.ConversationId.Value.ToString(),
            state = conversation.State.ToString().ToLowerInvariant(),
            security_profile = conversation.SecurityProfile.ToString(),
            membership_version = conversation.MembershipVersion,
            crypto_epoch = conversation.CryptoEpoch,
            active_human_count = activeHumans,
            message_status = messageStatus
        });
    }

    private async ValueTask<JsonElement> HandleGetHistoryAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Receive);
        var conversationId = RequireConversationId(arguments, "conversation_id");
        var limit = _options.MaxHistoryLimit;
        if (arguments.TryGetProperty("limit", out var limitProp) && limitProp.TryGetInt32(out var requestedLimit))
            limit = Math.Clamp(requestedLimit, 1, _options.MaxHistoryLimit);

        _ = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Receive, conversationId, null, cancellationToken).ConfigureAwait(false);

        if (_historyProvider is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var entries = await _historyProvider.GetParticipantScopedHistoryAsync(ctx.TenantId, conversationId, ctx.AgentId, limit, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            conversation_id = conversationId.Value.ToString(),
            count = entries.Count,
            messages = entries.Select(e => new
            {
                message_id = e.MessageId.Value.ToString(),
                sequence = e.Sequence,
                sender_kind = e.SenderKind.ToString().ToLowerInvariant(),
                sender = e.SenderDisplayId,
                content = e.PlaintextContent,
                created_at = e.CreatedAtUtc
            }).ToArray()
        });
    }

    private async ValueTask<JsonElement> HandleScheduleFollowUpAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.FollowUp);
        var conversationId = RequireConversationId(arguments, "conversation_id");
        var recipientPrincipalId = RequireUlidPrincipal(arguments, "recipient_principal_id");
        var dueAtText = RequireBoundedString(arguments, "due_at_utc", 64);
        var idempotencyKey = RequireBoundedString(arguments, "idempotency_key", 128);

        if (!DateTimeOffset.TryParse(dueAtText, out var dueAtUtc) || dueAtUtc <= _timeProvider.GetUtcNow())
            throw new ToolValidationException("due_at_utc must be a valid future UTC timestamp.");

        var maxAttempts = _options.MaxFollowUpAttempts;
        if (arguments.TryGetProperty("max_attempts", out var attemptsProp) && attemptsProp.TryGetInt32(out var requestedAttempts))
            maxAttempts = Math.Clamp(requestedAttempts, 1, _options.MaxFollowUpAttempts);

        var conversation = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        EnsureActiveHumanMember(conversation, recipientPrincipalId);

        if (_followUpCoordinator is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.FollowUp, conversationId, null, cancellationToken).ConfigureAwait(false);

        var record = await _followUpCoordinator.ScheduleFollowUpAsync(
            actor,
            conversationId,
            recipientPrincipalId,
            dueAtUtc.ToUniversalTime(),
            maxAttempts,
            idempotencyKey,
            ctx.CausalEventId ?? ctx.TaskId.Value.ToString(),
            ctx.TaskId,
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(new
        {
            followup_id = record.FollowUpId.Value.ToString(),
            conversation_id = record.ConversationId.Value.ToString(),
            state = record.State.ToString().ToLowerInvariant(),
            due_at_utc = record.DueAtUtc,
            max_attempts = record.MaxAttempts
        });
    }

    private async ValueTask<JsonElement> HandleCancelFollowUpAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.FollowUp);
        var followUpStr = RequireBoundedString(arguments, "followup_id", 64);
        if (!TryParseUlid(followUpStr, out var followUpUlid))
            throw new ToolValidationException("followup_id must be a valid ULID.");

        if (_followUpCoordinator is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        if (_followUpStore is not null)
        {
            var existing = await _followUpStore.GetFollowUpAsync(ctx.TenantId, new FollowUpId(followUpUlid), cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.TenantId != ctx.TenantId || existing.AgentId != ctx.AgentId)
                throw new ToolAuthorizationException(ErrorCodes.FollowUpNotFound);
        }

        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.FollowUp, null, null, cancellationToken).ConfigureAwait(false);

        var cancelled = await _followUpCoordinator.CancelFollowUpAsync(ctx.TenantId, new FollowUpId(followUpUlid), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            followup_id = cancelled.FollowUpId.Value.ToString(),
            state = cancelled.State.ToString().ToLowerInvariant()
        });
    }

    private async ValueTask<JsonElement> HandleSendVoiceNoteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Attachment);
        var conversationId = RequireConversationId(arguments, "conversation_id");
        var attachmentStr = RequireBoundedString(arguments, "attachment_id", 64);
        var clientOperationId = RequireBoundedString(arguments, "client_operation_id", 128);
        if (!TryParseUlid(attachmentStr, out var attachmentUlid))
            throw new ToolValidationException("attachment_id must be a valid ULID.");
        if (!arguments.TryGetProperty("duration_seconds", out var durProp) || !durProp.TryGetInt32(out var durationSeconds) || durationSeconds <= 0 || durationSeconds > 600)
            throw new ToolValidationException("duration_seconds must be between 1 and 600.");

        var conversation = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Attachment, conversationId, null, cancellationToken).ConfigureAwait(false);

        var voiceNotePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "voice_note",
            attachment_id = attachmentUlid.ToString(),
            duration_seconds = durationSeconds
        });
        var envelope = await EncryptAgentPayloadAsync(conversation, ctx, voiceNotePayload, cancellationToken).ConfigureAwait(false);
        var commit = await _messagingCoordinator.CommitMessageAsync(actor, conversationId, envelope, clientOperationId, ctx.CausalEventId, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(new
        {
            status = "committed",
            message_id = commit.Message.MessageId.Value.ToString(),
            attachment_id = attachmentUlid.ToString(),
            sequence = commit.Message.Sequence
        });
    }

    private async ValueTask<JsonElement> HandleStartCallAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Call);
        var conversationId = RequireConversationId(arguments, "conversation_id");

        _ = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Call, conversationId, null, cancellationToken).ConfigureAwait(false);

        if (_callCoordinator is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var mode = CallMediaSecurityMode.EndpointE2EE;
        if (arguments.TryGetProperty("media_security_mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String)
        {
            var raw = modeProp.GetString();
            if (string.Equals(raw, "transport-only", StringComparison.OrdinalIgnoreCase))
                mode = CallMediaSecurityMode.TransportOnly;
            else if (!string.Equals(raw, "endpoint-e2ee", StringComparison.OrdinalIgnoreCase))
                throw new ToolValidationException("Unsupported media_security_mode.");
        }

        var requireEncryption = _options.RequireEncryption && !_options.AllowUnencryptedFallback;
        if (requireEncryption && mode != CallMediaSecurityMode.EndpointE2EE)
            throw new ToolAuthorizationException(ErrorCodes.MediaEncryptionRequired);

        TimeSpan? timeout = null;
        if (arguments.TryGetProperty("ring_timeout_seconds", out var timeoutProp) && timeoutProp.TryGetInt32(out var secs) && secs > 0)
            timeout = TimeSpan.FromSeconds(Math.Min(secs, 120));

        var call = await _callCoordinator.CreateCallAsync(actor, conversationId, mode, requireEncryption, timeout, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            call_id = call.CallId.Value.ToString(),
            conversation_id = call.ConversationId.Value.ToString(),
            state = call.State.ToString().ToLowerInvariant(),
            media_security_mode = call.MediaSecurityMode.ToString(),
            expires_at = call.ExpiresAt
        });
    }

    private async ValueTask<JsonElement> HandleRequestApprovalAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Approval);
        var conversationId = RequireConversationId(arguments, "conversation_id");
        var targetHuman = RequireUlidPrincipal(arguments, "target_human_principal_id");
        var actionType = RequireBoundedString(arguments, "action_type", 128);
        var actionSummary = RequireBoundedString(arguments, "action_summary", 500);
        var parametersJson = arguments.TryGetProperty("parameters_json", out var pProp) && pProp.ValueKind == JsonValueKind.String
            ? pProp.GetString() ?? "{}"
            : "{}";
        if (parametersJson.Length > 4096)
            throw new ToolValidationException("parameters_json exceeds maximum length.");

        var conversation = await RequireHumanInclusiveConversationAsync(ctx.TenantId, conversationId, ctx.AgentId, cancellationToken).ConfigureAwait(false);
        EnsureActiveHumanMember(conversation, targetHuman);

        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Approval, conversationId, null, cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var lifetime = _options.ApprovalDefaultLifetime;
        if (arguments.TryGetProperty("expires_in_seconds", out var expProp) && expProp.TryGetInt32(out var expSecs) && expSecs > 0)
            lifetime = TimeSpan.FromSeconds(Math.Clamp(expSecs, 30, 3600));

        var signed = _approvalSigner.CreateSigned(
            new ApprovalRequestId(Ulid.NewUlid()),
            ctx.TenantId,
            conversationId,
            ctx.AgentId,
            targetHuman,
            actionType,
            actionSummary,
            parametersJson,
            now,
            now.Add(lifetime));

        if (_approvalStore is not null)
            signed = await _approvalStore.SaveRequestAsync(signed, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(new
        {
            approval_request_id = signed.ApprovalRequestId.Value.ToString(),
            conversation_id = signed.ConversationId.Value.ToString(),
            target_human_principal_id = signed.TargetHumanPrincipalId.Value.ToString(),
            action_type = signed.ActionType,
            action_summary = signed.ActionSummary,
            parameters_hash_sha256 = signed.CanonicalParametersHashSha256,
            expires_at_utc = signed.ExpiresAtUtc,
            signature = signed.SignatureHex,
            requires_explicit_human_confirmation = true
        });
    }

    private async ValueTask<JsonElement> HandleEscalateAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var ctx = RequireContext(MessagingPermission.Send);
        var conversationId = RequireConversationId(arguments, "conversation_id");
        var targetHuman = RequireUlidPrincipal(arguments, "target_human_principal_id");
        var reason = RequireBoundedString(arguments, "reason", 1000);
        var severity = arguments.TryGetProperty("severity", out var sevProp) && sevProp.ValueKind == JsonValueKind.String
            ? sevProp.GetString() ?? "normal"
            : "normal";
        if (severity is not ("low" or "normal" or "high" or "critical"))
            throw new ToolValidationException("severity must be one of: low, normal, high, critical.");

        var resolvedHuman = await _recipientResolver.ResolveHumanAsync(ctx.TenantId, ctx.ApplicationId, targetHuman, cancellationToken).ConfigureAwait(false);
        if (resolvedHuman is null || resolvedHuman.Kind != ParticipantKind.Human || resolvedHuman.TenantId != ctx.TenantId)
            throw new ToolAuthorizationException(ErrorCodes.Unauthorized);

        var actor = BuildAgentActor(ctx);
        await EnsureAuthorizedAsync(actor, MessagingPermission.Send, conversationId, resolvedHuman, cancellationToken).ConfigureAwait(false);

        if (_escalationHandler is null)
            throw new ToolAuthorizationException(ErrorCodes.CapabilityUnavailable);

        var result = await _escalationHandler.EscalateAsync(ctx.TenantId, conversationId, ctx.AgentId, targetHuman, reason, severity, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            escalation_id = result.EscalationId,
            conversation_id = result.ConversationId.Value.ToString(),
            target_human_principal_id = result.TargetHumanPrincipalId.Value.ToString(),
            status = result.Status,
            recorded_at_utc = result.RecordedAtUtc
        });
    }

    private MessagingToolExecutionContext RequireContext(MessagingPermission requiredPermission)
    {
        if (!_options.Enabled)
            throw new ToolAuthorizationException(ErrorCodes.MessagingDisabled);

        var msgCtx = MessagingToolExecutionContextScope.Current;
        var secCtx = SecurityToolExecutionContextScope.Current;

        MessagingToolExecutionContext ctx;
        if (msgCtx is not null)
        {
            // If ToolBroker also pushed SecurityToolExecutionContextScope, reconcile strictly to prevent spoofing.
            if (secCtx is not null &&
                (msgCtx.TenantId != secCtx.TenantId || msgCtx.AgentId != secCtx.AgentId || msgCtx.TaskId != secCtx.TaskId))
            {
                throw new ToolAuthorizationException(ErrorCodes.Unauthorized);
            }
            ctx = msgCtx;
        }
        else if (secCtx is not null)
        {
            ctx = new MessagingToolExecutionContext(
                secCtx.TenantId,
                secCtx.PrincipalId,
                secCtx.AgentId,
                secCtx.TaskId,
                _options.ApplicationId);
        }
        else
        {
            throw new ToolAuthorizationException(ErrorCodes.Unauthorized);
        }

        if (ctx.CausalDepth < 0 || ctx.CausalDepth > _options.MaxCausalDepth)
            throw new ToolAuthorizationException(ErrorCodes.ToolNotAllowed);

        if (!_options.HasPermission(ctx.AgentId, requiredPermission))
            throw new ToolAuthorizationException(ErrorCodes.ToolNotAllowed);

        return ctx;
    }

    private async ValueTask<ConversationRecord> RequireHumanInclusiveConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        AgentId agentId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationStore.GetConversationAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != tenantId)
            throw new ToolAuthorizationException(ErrorCodes.ConversationNotFound);

        if (conversation.State != ConversationState.Active)
            throw new ToolAuthorizationException(ErrorCodes.ConversationClosed);

        if (conversation.SecurityProfile == ConversationSecurityProfile.PrivateE2EE)
            throw new ToolAuthorizationException(ErrorCodes.PrivateE2EEAgentForbidden);

        var hasActiveHuman = conversation.Participants.Any(p => p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active);
        if (!hasActiveHuman)
            throw new ToolAuthorizationException(ErrorCodes.HumanParticipantRequired);

        var agentIsActiveMember = conversation.Participants.Any(p => p.Kind == ParticipantKind.Agent && p.AgentId == agentId && p.Status == ParticipantStatus.Active);
        if (!agentIsActiveMember)
            throw new ToolAuthorizationException(ErrorCodes.Unauthorized);

        return conversation;
    }

    private static void EnsureActiveHumanMember(ConversationRecord conversation, PrincipalId principalId)
    {
        var isMember = conversation.Participants.Any(p =>
            p.Kind == ParticipantKind.Human &&
            p.PrincipalId == principalId &&
            p.Status == ParticipantStatus.Active);
        if (!isMember)
            throw new ToolAuthorizationException(ErrorCodes.HumanParticipantRequired);
    }

    private async ValueTask EnsureAuthorizedAsync(
        MessagingActor actor,
        MessagingPermission permission,
        ConversationId? conversationId,
        MessagingActor? target,
        CancellationToken cancellationToken)
    {
        var decision = await _authorizer.AuthorizeAsync(actor, permission, conversationId, target, cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
            throw new ToolAuthorizationException(decision.ErrorCode ?? ErrorCodes.Unauthorized);
    }

    private async ValueTask<EncryptedMessageEnvelope> EncryptAgentPayloadAsync(
        ConversationRecord conversation,
        MessagingToolExecutionContext ctx,
        byte[] plaintextBytes,
        CancellationToken cancellationToken)
    {
        var senderDeviceId = ctx.AgentDeviceId ?? new DeviceId(Ulid.NewUlid());
        if (_cryptoProvider is not null)
        {
            var aad = Encoding.UTF8.GetBytes($"{conversation.TenantId.Value}|{conversation.ConversationId.Value}|{conversation.CryptoEpoch}");
            return await _cryptoProvider.EncryptAsync(conversation.ConversationId, senderDeviceId, plaintextBytes, aad, cancellationToken).ConfigureAwait(false);
        }

        if (_options.RequireEncryption && !_options.AllowUnencryptedFallback)
            throw new ToolAuthorizationException(ErrorCodes.CryptoVerificationFailed);

        return new EncryptedMessageEnvelope(
            "governed-plaintext-v1",
            conversation.CryptoEpoch,
            Convert.ToBase64String(plaintextBytes),
            string.Empty,
            string.Empty);
    }

    private static MessagingActor BuildAgentActor(MessagingToolExecutionContext ctx) =>
        new(ctx.TenantId, ctx.ApplicationId, ParticipantKind.Agent, null, ctx.AgentId, ctx.AgentDeviceId);

    private static ConversationId RequireConversationId(JsonElement arguments, string property)
    {
        var raw = RequireBoundedString(arguments, property, 64);
        if (!TryParseUlid(raw, out var ulid))
            throw new ToolValidationException($"'{property}' must be a valid ULID.");
        return new ConversationId(ulid);
    }

    private static PrincipalId RequireUlidPrincipal(JsonElement arguments, string property)
    {
        var raw = RequireBoundedString(arguments, property, 64);
        if (!TryParseUlid(raw, out var ulid))
            throw new ToolValidationException($"'{property}' must be a valid ULID.");
        return new PrincipalId(ulid);
    }

    private static bool TryParseUlid(string? value, out Ulid ulid)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ulid = default;
            return false;
        }
        try
        {
            ulid = Ulid.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            ulid = default;
            return false;
        }
    }

    private static string RequireBoundedString(JsonElement arguments, string property, int maxLength)
    {
        if (!arguments.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            throw new ToolValidationException($"'{property}' is required.");
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ToolValidationException($"'{property}' must be non-empty and at most {maxLength} characters.");
        return value;
    }
}
