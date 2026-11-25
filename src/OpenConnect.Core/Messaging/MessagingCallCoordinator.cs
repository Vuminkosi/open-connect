using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Core.Messaging;

/// <summary>
/// Coordinates voice/media call invitations, first-device-accept CAS, no auto-answer,
/// human-inclusive enforcement, and fail-closed E2EE media boundary validation.
/// </summary>
public sealed class MessagingCallCoordinator : IMessagingCallCoordinator
{
    public static readonly TimeSpan DefaultRingTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MaxRingTimeout = TimeSpan.FromMinutes(3);

    private readonly IMessagingCallStore _callStore;
    private readonly IMessagingConversationStore _conversationStore;
    private readonly IMessagingAuthorizer _authorizer;
    private readonly TimeProvider _timeProvider;

    public MessagingCallCoordinator(
        IMessagingCallStore callStore,
        IMessagingConversationStore conversationStore,
        IMessagingAuthorizer authorizer,
        TimeProvider? timeProvider = null)
    {
        _callStore = callStore ?? throw new ArgumentNullException(nameof(callStore));
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<MessagingCallRecord> CreateCallAsync(
        MessagingActor caller,
        ConversationId conversationId,
        CallMediaSecurityMode mediaSecurityMode,
        bool requireEncryption,
        TimeSpan? ringTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!Enum.IsDefined(mediaSecurityMode))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Media security mode is invalid.", 400);

        var timeout = ringTimeout ?? DefaultRingTimeout;
        if (timeout <= TimeSpan.Zero || timeout > MaxRingTimeout)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Ring timeout is out of bounds.", 400);

        var conversation = await _conversationStore.GetConversationAsync(caller.TenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != caller.TenantId ||
            !string.Equals(conversation.ApplicationId, caller.ApplicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Conversation was not found.", 404);
        }

        if (conversation.State != ConversationState.Active)
            throw new MessagingDomainException(ErrorCodes.ConversationClosed, "Cannot start a call in a closed conversation.", 409);

        if (!IsActiveParticipant(conversation, caller))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Caller is not an active conversation participant.", 403);

        var auth = await _authorizer.AuthorizeAsync(caller, MessagingPermission.Call, conversationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Call initiation denied.", 403);

        // Enforce mandatory human-inclusive invariant for calls (no agent-only calls).
        var activeHumans = conversation.Participants
            .Where(p => p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active && p.PrincipalId is not null)
            .Select(p => p.PrincipalId!.Value)
            .Distinct()
            .ToArray();

        if (activeHumans.Length == 0)
        {
            throw new MessagingDomainException(
                ErrorCodes.HumanParticipantRequired,
                "Calls require at least one active human participant; agent-only calls are prohibited.",
                409);
        }

        var allowedRecipients = caller.Kind == ParticipantKind.Human && caller.PrincipalId is not null
            ? activeHumans.Where(h => h != caller.PrincipalId.Value).ToArray()
            : activeHumans;

        if (caller.Kind == ParticipantKind.Agent && allowedRecipients.Length == 0)
        {
            throw new MessagingDomainException(
                ErrorCodes.HumanParticipantRequired,
                "Agent-initiated calls require at least one authorized human recipient.",
                409);
        }

        // Fail-closed E2EE media boundary check: never silently substitute transport TLS for E2EE.
        var mustEncryptMedia = requireEncryption || conversation.SecurityProfile == ConversationSecurityProfile.PrivateE2EE;
        if (mustEncryptMedia && mediaSecurityMode != CallMediaSecurityMode.EndpointE2EE)
        {
            throw new MessagingDomainException(
                ErrorCodes.MediaEncryptionRequired,
                "Policy requires endpoint E2EE media, which is not supported by a transport-only media backend.",
                409);
        }

        var now = _timeProvider.GetUtcNow();
        var record = new MessagingCallRecord(
            caller.TenantId,
            caller.ApplicationId,
            new CallId(Ulid.NewUlid()),
            conversationId,
            caller.Kind,
            caller.PrincipalId,
            caller.AgentId,
            allowedRecipients.Length > 0 ? allowedRecipients : activeHumans,
            CallState.Invited,
            mediaSecurityMode,
            now,
            now.Add(timeout));

        return await _callStore.CreateCallAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<MessagingCallRecord> MarkRingingAsync(
        TenantId tenantId,
        CallId callId,
        CancellationToken cancellationToken = default)
    {
        var call = await _callStore.GetCallAsync(tenantId, callId, cancellationToken).ConfigureAwait(false);
        if (call is null || call.TenantId != tenantId)
            throw new MessagingDomainException(ErrorCodes.CallNotFound, "Call was not found.", 404);

        var now = _timeProvider.GetUtcNow();
        if (now >= call.ExpiresAt)
        {
            var missed = call with { State = CallState.Missed, EndedAt = now, EndReason = "ring_timeout" };
            await _callStore.TryTransitionCallAsync(missed, call.State, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(ErrorCodes.CallInvalidState, "Call invitation expired.", 410);
        }

        if (call.State != CallState.Invited)
            throw new MessagingDomainException(ErrorCodes.CallInvalidState, $"Cannot mark call ringing from state {call.State}.", 409);

        var ringing = call with { State = CallState.Ringing };
        var wonCas = await _callStore.TryTransitionCallAsync(ringing, CallState.Invited, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Call state changed concurrently.", 409);

        return ringing;
    }

    public async ValueTask<MessagingCallRecord> AcceptCallAsync(
        MessagingActor acceptor,
        CallId callId,
        SessionId voiceSessionId,
        string admissionTokenHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptor);
        if (acceptor.Kind != ParticipantKind.Human || acceptor.PrincipalId is null || acceptor.PrincipalId.Value.Value == default ||
            acceptor.DeviceId is null || acceptor.DeviceId.Value.Value == default)
        {
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Only an authenticated human device may accept a call.", 403);
        }

        if (voiceSessionId.Value == default || string.IsNullOrWhiteSpace(admissionTokenHash))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "A scoped voice session and admission token hash are required.", 400);

        var call = await _callStore.GetCallAsync(acceptor.TenantId, callId, cancellationToken).ConfigureAwait(false);
        if (call is null || call.TenantId != acceptor.TenantId ||
            !string.Equals(call.ApplicationId, acceptor.ApplicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.CallNotFound, "Call was not found.", 404);
        }

        if (!call.AllowedHumanRecipients.Contains(acceptor.PrincipalId.Value))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Principal is not an authorized recipient of this call.", 403);

        var auth = await _authorizer.AuthorizeAsync(acceptor, MessagingPermission.Call, call.ConversationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Call acceptance denied.", 403);

        var now = _timeProvider.GetUtcNow();
        if (now >= call.ExpiresAt)
        {
            if (call.State is CallState.Invited or CallState.Ringing)
            {
                var missed = call with { State = CallState.Missed, EndedAt = now, EndReason = "ring_timeout" };
                await _callStore.TryTransitionCallAsync(missed, call.State, cancellationToken).ConfigureAwait(false);
            }
            throw new MessagingDomainException(ErrorCodes.CallInvalidState, "Call invitation has expired.", 410);
        }

        if (call.State is not (CallState.Invited or CallState.Ringing))
            throw new MessagingDomainException(ErrorCodes.CallInvalidState, "Call is no longer available to accept.", 409);

        var accepted = call with
        {
            State = CallState.Accepted,
            AcceptedDeviceId = acceptor.DeviceId,
            AcceptedPrincipalId = acceptor.PrincipalId,
            VoiceSessionId = voiceSessionId,
            AdmissionTokenHash = admissionTokenHash
        };

        var wonCas = await _callStore.TryTransitionCallAsync(accepted, call.State, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Call was already accepted or ended on another device.", 409);

        return accepted;
    }

    public async ValueTask<MessagingCallRecord> DeclineCallAsync(
        MessagingActor actor,
        CallId callId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var call = await _callStore.GetCallAsync(actor.TenantId, callId, cancellationToken).ConfigureAwait(false);
        if (call is null || call.TenantId != actor.TenantId)
            throw new MessagingDomainException(ErrorCodes.CallNotFound, "Call was not found.", 404);

        if (actor.Kind != ParticipantKind.Human || actor.PrincipalId is null || !call.AllowedHumanRecipients.Contains(actor.PrincipalId.Value))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Only an invited human recipient may decline a call.", 403);

        if (call.State is not (CallState.Invited or CallState.Ringing))
            throw new MessagingDomainException(ErrorCodes.CallInvalidState, "Call cannot be declined in its current state.", 409);

        var declined = call with
        {
            State = CallState.Declined,
            EndedAt = _timeProvider.GetUtcNow(),
            EndReason = string.IsNullOrWhiteSpace(reason) ? "declined" : reason
        };

        var wonCas = await _callStore.TryTransitionCallAsync(declined, call.State, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Call state changed concurrently.", 409);

        return declined;
    }

    public async ValueTask<MessagingCallRecord> EndCallAsync(
        MessagingActor actor,
        CallId callId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var call = await _callStore.GetCallAsync(actor.TenantId, callId, cancellationToken).ConfigureAwait(false);
        if (call is null || call.TenantId != actor.TenantId)
            throw new MessagingDomainException(ErrorCodes.CallNotFound, "Call was not found.", 404);

        var isCaller = (actor.Kind == call.CallerKind) &&
            ((actor.Kind == ParticipantKind.Human && actor.PrincipalId == call.CallerPrincipalId) ||
             (actor.Kind == ParticipantKind.Agent && actor.AgentId == call.CallerAgentId));
        var isRecipient = actor.Kind == ParticipantKind.Human && actor.PrincipalId is not null &&
            call.AllowedHumanRecipients.Contains(actor.PrincipalId.Value);

        if (!isCaller && !isRecipient)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Actor is not authorized to end this call.", 403);

        if (IsTerminalState(call.State))
            return call;

        var targetState = call.State is CallState.Invited or CallState.Ringing ? CallState.Cancelled : CallState.Ended;
        var ended = call with
        {
            State = targetState,
            EndedAt = _timeProvider.GetUtcNow(),
            EndReason = string.IsNullOrWhiteSpace(reason) ? (targetState == CallState.Cancelled ? "caller_cancelled" : "ended") : reason
        };

        var wonCas = await _callStore.TryTransitionCallAsync(ended, call.State, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Call state changed concurrently.", 409);

        return ended;
    }

    public async ValueTask<int> TerminateCallsForConversationAsync(
        TenantId tenantId,
        ConversationId conversationId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var activeCalls = await _callStore.GetActiveCallsForConversationAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        var terminated = 0;
        var now = _timeProvider.GetUtcNow();

        foreach (var call in activeCalls)
        {
            if (IsTerminalState(call.State))
                continue;

            var targetState = call.State is CallState.Invited or CallState.Ringing ? CallState.Cancelled : CallState.Ended;
            var updated = call with
            {
                State = targetState,
                EndedAt = now,
                EndReason = string.IsNullOrWhiteSpace(reason) ? "last_human_left" : reason
            };

            if (await _callStore.TryTransitionCallAsync(updated, call.State, cancellationToken).ConfigureAwait(false))
                terminated++;
        }

        return terminated;
    }

    public static bool IsTerminalState(CallState state) =>
        state is CallState.Ended or CallState.Declined or CallState.Missed or CallState.Cancelled or CallState.Failed;

    private static bool IsActiveParticipant(ConversationRecord conversation, MessagingActor actor) =>
        conversation.Participants.Any(p =>
            p.Status == ParticipantStatus.Active &&
            p.Kind == actor.Kind &&
            ((actor.Kind == ParticipantKind.Human && p.PrincipalId == actor.PrincipalId) ||
             (actor.Kind == ParticipantKind.Agent && p.AgentId == actor.AgentId)));
}
