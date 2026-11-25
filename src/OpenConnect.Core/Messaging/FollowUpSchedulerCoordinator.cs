using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Core.Messaging;

/// <summary>
/// Coordinates durable due-time follow-ups with bounded batch claiming, lease fencing,
/// quiet-hours and max-attempt enforcement, and atomic CAS resolution of due follow-up vs human reply races.
/// </summary>
public sealed class FollowUpSchedulerCoordinator : IFollowUpSchedulerCoordinator
{
    public const int MaxAllowedAttemptsLimit = 20;

    private readonly IFollowUpStore _followUpStore;
    private readonly IMessagingConversationStore _conversationStore;
    private readonly IMessagingAuthorizer _authorizer;
    private readonly TimeProvider _timeProvider;

    public FollowUpSchedulerCoordinator(
        IFollowUpStore followUpStore,
        IMessagingConversationStore conversationStore,
        IMessagingAuthorizer authorizer,
        TimeProvider? timeProvider = null)
    {
        _followUpStore = followUpStore ?? throw new ArgumentNullException(nameof(followUpStore));
        _conversationStore = conversationStore ?? throw new ArgumentNullException(nameof(conversationStore));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<FollowUpRecord> ScheduleFollowUpAsync(
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentActor);
        if (agentActor.Kind != ParticipantKind.Agent || agentActor.AgentId is null || string.IsNullOrWhiteSpace(agentActor.AgentId.Value.Value))
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Only an authorized operational agent may schedule follow-ups.", 403);
        if (recipientPrincipalId.Value == default)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "A valid human recipient is required.", 400);
        if (maxAttempts is < 1 or > MaxAllowedAttemptsLimit)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, $"MaxAttempts must be between 1 and {MaxAllowedAttemptsLimit}.", 400);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(causalEventId))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "IdempotencyKey and CausalEventId are required.", 400);

        var conversation = await _conversationStore.GetConversationAsync(agentActor.TenantId, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.TenantId != agentActor.TenantId ||
            !string.Equals(conversation.ApplicationId, agentActor.ApplicationId, StringComparison.Ordinal))
        {
            throw new MessagingDomainException(ErrorCodes.ConversationNotFound, "Conversation was not found.", 404);
        }

        if (conversation.State != ConversationState.Active)
            throw new MessagingDomainException(ErrorCodes.ConversationClosed, "Cannot schedule follow-ups on a closed conversation.", 409);

        var hasActiveAgent = conversation.Participants.Any(p =>
            p.Status == ParticipantStatus.Active && p.Kind == ParticipantKind.Agent && p.AgentId == agentActor.AgentId);
        var hasActiveHuman = conversation.Participants.Any(p =>
            p.Status == ParticipantStatus.Active && p.Kind == ParticipantKind.Human && p.PrincipalId == recipientPrincipalId);

        if (!hasActiveAgent || !hasActiveHuman)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, "Agent and human recipient must both be active conversation participants.", 403);

        var targetActor = new MessagingActor(agentActor.TenantId, agentActor.ApplicationId, ParticipantKind.Human, PrincipalId: recipientPrincipalId);
        var auth = await _authorizer.AuthorizeAsync(agentActor, MessagingPermission.FollowUp, conversationId, targetActor, cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Follow-up scheduling denied.", 403);

        var record = new FollowUpRecord(
            agentActor.TenantId,
            agentActor.ApplicationId,
            new FollowUpId(Ulid.NewUlid()),
            conversationId,
            taskId,
            agentActor.AgentId.Value,
            recipientPrincipalId,
            dueAtUtc.ToUniversalTime(),
            Attempt: 0,
            MaxAttempts: maxAttempts,
            State: FollowUpState.Scheduled,
            IdempotencyKey: idempotencyKey,
            CausalEventId: causalEventId,
            QuietHoursStartUtc: quietHoursStartUtc,
            QuietHoursEndUtc: quietHoursEndUtc);

        return await _followUpStore.CreateFollowUpAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<FollowUpRecord>> ClaimDueBatchAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        int maxBatchSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner))
            throw new ArgumentException("Lease owner is required.", nameof(leaseOwner));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (maxBatchSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize));

        return _followUpStore.ClaimDueFollowUpsAsync(
            _timeProvider.GetUtcNow(),
            leaseOwner,
            leaseDuration,
            maxBatchSize,
            cancellationToken);
    }

    public async ValueTask<FollowUpRecord> AuthorizeAndDispatchAsync(
        TenantId tenantId,
        FollowUpId followUpId,
        string leaseOwner,
        long leaseFence,
        Func<FollowUpRecord, CancellationToken, ValueTask<bool>> dispatchCallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatchCallback);
        var record = await _followUpStore.GetFollowUpAsync(tenantId, followUpId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.TenantId != tenantId)
            throw new MessagingDomainException(ErrorCodes.FollowUpNotFound, "Follow-up was not found.", 404);

        var now = _timeProvider.GetUtcNow();
        if (record.State != FollowUpState.Claimed ||
            !string.Equals(record.LeaseOwner, leaseOwner, StringComparison.Ordinal) ||
            record.LeaseFence != leaseFence ||
            record.LeaseExpiresAtUtc is null ||
            record.LeaseExpiresAtUtc.Value <= now)
        {
            throw new MessagingDomainException(ErrorCodes.Conflict, "Follow-up lease is no longer valid or fence mismatch.", 409);
        }

        // Enforce max attempts before dispatch
        if (record.Attempt >= record.MaxAttempts)
        {
            var escalated = record with
            {
                State = FollowUpState.Escalated,
                CompletedAtUtc = now
            };
            await _followUpStore.TryTransitionFollowUpAsync(escalated, FollowUpState.Claimed, leaseFence, cancellationToken).ConfigureAwait(false);
            return escalated;
        }

        // Enforce quiet hours (reschedule to end of quiet window instead of dispatching)
        if (record.QuietHoursStartUtc is not null && record.QuietHoursEndUtc is not null &&
            IsWithinQuietHours(TimeOnly.FromDateTime(now.UtcDateTime), record.QuietHoursStartUtc.Value, record.QuietHoursEndUtc.Value))
        {
            var nextDue = ComputeNextDueAfterQuietHours(now, record.QuietHoursEndUtc.Value);
            var rescheduled = record with
            {
                State = FollowUpState.Rescheduled,
                DueAtUtc = nextDue,
                LeaseOwner = null,
                LeaseExpiresAtUtc = null
            };
            var wonQuietCas = await _followUpStore.TryTransitionFollowUpAsync(rescheduled, FollowUpState.Claimed, leaseFence, cancellationToken).ConfigureAwait(false);
            if (!wonQuietCas)
                throw new MessagingDomainException(ErrorCodes.Conflict, "Follow-up state changed concurrently during quiet-hours reschedule.", 409);
            return rescheduled;
        }

        // Recheck conversation and human participation at execution time
        var conversation = await _conversationStore.GetConversationAsync(tenantId, record.ConversationId, cancellationToken).ConfigureAwait(false);
        var humanStillActive = conversation is not null &&
            conversation.State == ConversationState.Active &&
            conversation.Participants.Any(p => p.Kind == ParticipantKind.Human && p.Status == ParticipantStatus.Active && p.PrincipalId == record.RecipientPrincipalId);

        if (!humanStillActive)
        {
            var cancelled = record with { State = FollowUpState.Cancelled, CompletedAtUtc = now };
            await _followUpStore.TryTransitionFollowUpAsync(cancelled, FollowUpState.Claimed, leaseFence, cancellationToken).ConfigureAwait(false);
            return cancelled;
        }

        // Fresh authorization check at execution time
        var agentActor = new MessagingActor(record.TenantId, record.ApplicationId, ParticipantKind.Agent, AgentId: record.AgentId);
        var targetActor = new MessagingActor(record.TenantId, record.ApplicationId, ParticipantKind.Human, PrincipalId: record.RecipientPrincipalId);
        var auth = await _authorizer.AuthorizeAsync(agentActor, MessagingPermission.FollowUp, record.ConversationId, targetActor, cancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
        {
            var cancelled = record with { State = FollowUpState.Cancelled, CompletedAtUtc = now };
            await _followUpStore.TryTransitionFollowUpAsync(cancelled, FollowUpState.Claimed, leaseFence, cancellationToken).ConfigureAwait(false);
            throw new MessagingDomainException(auth.ErrorCode ?? ErrorCodes.Unauthorized, auth.Reason ?? "Follow-up execution authorization denied.", 403);
        }

        // CAS Claimed -> Authorized (if human replied concurrently, this CAS fails and prevents stale dispatch!)
        var authorized = record with { State = FollowUpState.Authorized };
        var wonAuthCas = await _followUpStore.TryTransitionFollowUpAsync(authorized, FollowUpState.Claimed, leaseFence, cancellationToken).ConfigureAwait(false);
        if (!wonAuthCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Follow-up was superseded concurrently before dispatch.", 409);

        var dispatchedOk = await dispatchCallback(authorized, cancellationToken).ConfigureAwait(false);
        if (!dispatchedOk)
        {
            var timedOut = authorized with { State = FollowUpState.TimedOut };
            await _followUpStore.TryTransitionFollowUpAsync(timedOut, FollowUpState.Authorized, leaseFence, cancellationToken).ConfigureAwait(false);
            return timedOut;
        }

        var dispatched = authorized with
        {
            State = FollowUpState.Dispatched,
            Attempt = authorized.Attempt + 1,
            LastDispatchedAtUtc = now
        };
        if (!await _followUpStore.TryTransitionFollowUpAsync(dispatched, FollowUpState.Authorized, leaseFence, cancellationToken).ConfigureAwait(false))
            throw new MessagingDomainException(ErrorCodes.Conflict, "Follow-up state changed during dispatch.", 409);

        var waiting = dispatched with { State = FollowUpState.Waiting };
        await _followUpStore.TryTransitionFollowUpAsync(waiting, FollowUpState.Dispatched, leaseFence, cancellationToken).ConfigureAwait(false);
        return waiting;
    }

    public async ValueTask<FollowUpRaceResolution> RecordHumanReplyAsync(
        TenantId tenantId,
        ConversationId conversationId,
        PrincipalId humanPrincipalId,
        string triggeringEventId,
        SessionId? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(triggeringEventId))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "TriggeringEventId is required.", 400);

        var activeFollowUps = await _followUpStore.GetActiveFollowUpsForConversationAsync(
            tenantId, conversationId, humanPrincipalId, cancellationToken).ConfigureAwait(false);

        if (activeFollowUps.Count == 0)
            throw new MessagingDomainException(ErrorCodes.FollowUpNotFound, "No active follow-up found for this conversation and recipient.", 404);

        var now = _timeProvider.GetUtcNow();
        var primary = activeFollowUps[0];
        var suppressed = primary.State is FollowUpState.Scheduled or FollowUpState.Claimed or FollowUpState.Authorized;

        var received = primary with
        {
            State = FollowUpState.ResponseReceived,
            LeaseOwner = null,
            LeaseExpiresAtUtc = null
        };

        var wonCas = await _followUpStore.TryTransitionFollowUpAsync(received, primary.State, primary.LeaseFence, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Follow-up state changed concurrently while recording human reply.", 409);

        var executionKey = $"{conversationId.Value}:{triggeringEventId}:{primary.AgentId.Value}";
        var continuation = new ContinuationRecord(
            tenantId,
            new ContinuationId(Ulid.NewUlid()),
            conversationId,
            triggeringEventId,
            primary.AgentId,
            humanPrincipalId,
            primary.TaskId,
            sessionId,
            ContinuationStatus.Queued,
            executionKey,
            CreatedAt: now);

        var persistedContinuation = await _followUpStore.CreateContinuationAsync(continuation, cancellationToken).ConfigureAwait(false);

        var queued = received with { State = FollowUpState.ContinuationQueued };
        await _followUpStore.TryTransitionFollowUpAsync(queued, FollowUpState.ResponseReceived, primary.LeaseFence, cancellationToken).ConfigureAwait(false);

        var completed = queued with { State = FollowUpState.Completed, CompletedAtUtc = now };
        await _followUpStore.TryTransitionFollowUpAsync(completed, FollowUpState.ContinuationQueued, primary.LeaseFence, cancellationToken).ConfigureAwait(false);

        return new FollowUpRaceResolution(completed, suppressed, persistedContinuation);
    }

    public async ValueTask<FollowUpRecord> CancelFollowUpAsync(
        TenantId tenantId,
        FollowUpId followUpId,
        CancellationToken cancellationToken = default)
    {
        var record = await _followUpStore.GetFollowUpAsync(tenantId, followUpId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.TenantId != tenantId)
            throw new MessagingDomainException(ErrorCodes.FollowUpNotFound, "Follow-up was not found.", 404);

        if (record.State is FollowUpState.Completed or FollowUpState.Cancelled or FollowUpState.Expired or FollowUpState.Escalated)
            return record;

        var cancelled = record with
        {
            State = FollowUpState.Cancelled,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            LeaseOwner = null,
            LeaseExpiresAtUtc = null
        };

        var wonCas = await _followUpStore.TryTransitionFollowUpAsync(cancelled, record.State, record.LeaseFence, cancellationToken).ConfigureAwait(false);
        if (!wonCas)
            throw new MessagingDomainException(ErrorCodes.Conflict, "Follow-up state changed concurrently.", 409);

        return cancelled;
    }

    public static bool IsWithinQuietHours(TimeOnly currentUtc, TimeOnly startUtc, TimeOnly endUtc)
    {
        if (startUtc == endUtc)
            return false;
        if (startUtc < endUtc)
            return currentUtc >= startUtc && currentUtc < endUtc;
        // Overnight window (e.g., 22:00 -> 07:00 UTC)
        return currentUtc >= startUtc || currentUtc < endUtc;
    }

    private static DateTimeOffset ComputeNextDueAfterQuietHours(DateTimeOffset nowUtc, TimeOnly quietEndUtc)
    {
        var candidate = new DateTimeOffset(
            nowUtc.Year, nowUtc.Month, nowUtc.Day,
            quietEndUtc.Hour, quietEndUtc.Minute, quietEndUtc.Second,
            TimeSpan.Zero);
        if (candidate <= nowUtc)
            candidate = candidate.AddDays(1);
        return candidate;
    }
}
