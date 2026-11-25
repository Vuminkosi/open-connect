using System.Collections.Concurrent;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Messaging;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class MessagingCallAndFollowUpTests
{
    private readonly TenantId _tenant = new(Ulid.NewUlid());
    private readonly PrincipalId _human = new(Ulid.NewUlid());
    private readonly AgentId _agent = new("ops-agent-1");

    [Fact]
    public async Task A25_And_A27_CallCoordinator_EnforcesMediaE2EEBoundary_AndFirstDeviceAcceptCas()
    {
        var clock = new MessagingDeviceEnrollmentTests.MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));
        var convoStore = new MessagingConversationCoordinatorTests.InMemoryConversationStore();
        var callStore = new InMemoryCallStore();
        var auth = new AllowAllAuth();
        var coordinator = new MessagingCallCoordinator(callStore, convoStore, auth, clock);

        var convo = await SeedConversationAsync(convoStore, ConversationSecurityProfile.AgentE2EE);
        var agentActor = new MessagingActor(_tenant, "crm", ParticipantKind.Agent, AgentId: _agent);

        // A27: E2EE-required call on TransportOnly media backend fails closed
        var e2eeEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CreateCallAsync(agentActor, convo.ConversationId, CallMediaSecurityMode.TransportOnly, requireEncryption: true));
        Assert.Equal(ErrorCodes.MediaEncryptionRequired, e2eeEx.ErrorCode);
        Assert.Equal(409, e2eeEx.StatusCode);

        // Allowed when EndpointE2EE is provided
        var call = await coordinator.CreateCallAsync(agentActor, convo.ConversationId, CallMediaSecurityMode.EndpointE2EE, requireEncryption: true);
        Assert.Equal(CallState.Invited, call.State);

        var ringing = await coordinator.MarkRingingAsync(_tenant, call.CallId);
        Assert.Equal(CallState.Ringing, ringing.State);

        // A25: First authorized human device wins CAS; second device loses
        var dev1 = new DeviceId(Ulid.NewUlid());
        var dev2 = new DeviceId(Ulid.NewUlid());
        var humanDev1 = new MessagingActor(_tenant, "crm", ParticipantKind.Human, PrincipalId: _human, DeviceId: dev1);
        var humanDev2 = new MessagingActor(_tenant, "crm", ParticipantKind.Human, PrincipalId: _human, DeviceId: dev2);

        var accepted = await coordinator.AcceptCallAsync(humanDev1, call.CallId, new SessionId(Ulid.NewUlid()), "adm-hash-1");
        Assert.Equal(CallState.Accepted, accepted.State);
        Assert.Equal(dev1, accepted.AcceptedDeviceId);

        var lateEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.AcceptCallAsync(humanDev2, call.CallId, new SessionId(Ulid.NewUlid()), "adm-hash-2"));
        Assert.Equal(ErrorCodes.CallInvalidState, lateEx.ErrorCode);

        // Terminate calls for conversation when last human leaves
        var terminated = await coordinator.TerminateCallsForConversationAsync(_tenant, convo.ConversationId, "last_human_left");
        Assert.Equal(1, terminated);
        var afterTerm = await callStore.GetCallAsync(_tenant, call.CallId);
        Assert.Equal(CallState.Ended, afterTerm!.State);
    }

    [Fact]
    public async Task A24_FollowUpScheduler_ResolvesDueVsHumanReplyRaceAtomically_AndEnforcesQuietHoursAndMaxAttempts()
    {
        var clock = new MessagingDeviceEnrollmentTests.MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero));
        var convoStore = new MessagingConversationCoordinatorTests.InMemoryConversationStore();
        var followUpStore = new InMemoryFollowUpStore();
        var coordinator = new FollowUpSchedulerCoordinator(followUpStore, convoStore, new AllowAllAuth(), clock);

        var convo = await SeedConversationAsync(convoStore, ConversationSecurityProfile.AgentE2EE);
        var agentActor = new MessagingActor(_tenant, "crm", ParticipantKind.Agent, AgentId: _agent);

        // Schedule due follow-up
        var fu = await coordinator.ScheduleFollowUpAsync(
            agentActor, convo.ConversationId, _human,
            dueAtUtc: clock.GetUtcNow().AddMinutes(5),
            maxAttempts: 2, idempotencyKey: "fu-1", causalEventId: "evt-0");
        Assert.Equal(FollowUpState.Scheduled, fu.State);

        // Advance to due time and claim
        clock.Advance(TimeSpan.FromMinutes(6));
        var claimed = await coordinator.ClaimDueBatchAsync("worker-1", TimeSpan.FromMinutes(2), 10);
        Assert.Single(claimed);
        Assert.Equal(FollowUpState.Claimed, claimed[0].State);

        // Human replies concurrently BEFORE dispatch!
        var resolution = await coordinator.RecordHumanReplyAsync(_tenant, convo.ConversationId, _human, "evt-human-reply-1");
        Assert.True(resolution.SuppressedByHumanReply);
        Assert.Equal(FollowUpState.Completed, resolution.FollowUp.State);
        Assert.NotNull(resolution.QueuedContinuation);

        // Stale dispatch callback must NOT run — CAS fails with 409 Conflict!
        var callbackInvoked = false;
        var raceEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.AuthorizeAndDispatchAsync(_tenant, claimed[0].FollowUpId, "worker-1", claimed[0].LeaseFence, (_, _) =>
            {
                callbackInvoked = true;
                return ValueTask.FromResult(true);
            }));
        Assert.Equal(ErrorCodes.Conflict, raceEx.ErrorCode);
        Assert.False(callbackInvoked);

        // Quiet hours test (overnight 22:00 - 07:00 UTC, test at 23:30 UTC)
        var nightClock = new MessagingDeviceEnrollmentTests.MutableTimeProvider(new DateTimeOffset(2026, 11, 25, 23, 30, 0, TimeSpan.Zero));
        var nightCoord = new FollowUpSchedulerCoordinator(followUpStore, convoStore, new AllowAllAuth(), nightClock);
        var quietFu = await nightCoord.ScheduleFollowUpAsync(
            agentActor, convo.ConversationId, _human,
            dueAtUtc: nightClock.GetUtcNow(),
            maxAttempts: 1, idempotencyKey: "fu-quiet", causalEventId: "evt-q",
            quietHoursStartUtc: new TimeOnly(22, 0), quietHoursEndUtc: new TimeOnly(7, 0));
        var quietClaimed = await nightCoord.ClaimDueBatchAsync("worker-2", TimeSpan.FromMinutes(2), 10);
        var target = quietClaimed.Single(x => x.FollowUpId == quietFu.FollowUpId);
        var dispatchedInQuiet = false;
        var rescheduled = await nightCoord.AuthorizeAndDispatchAsync(_tenant, target.FollowUpId, "worker-2", target.LeaseFence, (_, _) =>
        {
            dispatchedInQuiet = true;
            return ValueTask.FromResult(true);
        });
        Assert.False(dispatchedInQuiet);
        Assert.Equal(FollowUpState.Rescheduled, rescheduled.State);
        Assert.Equal(7, rescheduled.DueAtUtc.Hour);
    }

    private async Task<ConversationRecord> SeedConversationAsync(
        MessagingConversationCoordinatorTests.InMemoryConversationStore store,
        ConversationSecurityProfile profile)
    {
        var cid = new ConversationId(Ulid.NewUlid());
        var convo = new ConversationRecord(
            _tenant, "crm", cid, profile, ConversationState.Active, 1, 1, 1, DateTimeOffset.UtcNow,
            [
                new ConversationParticipant(cid, ParticipantKind.Human, _human, null, ParticipantRole.Owner, ParticipantStatus.Active, 1),
                new ConversationParticipant(cid, ParticipantKind.Agent, null, _agent, ParticipantRole.OperationalAgent, ParticipantStatus.Active, 1)
            ]);
        return await store.CreateConversationAsync(convo);
    }

    private sealed class AllowAllAuth : IMessagingAuthorizer
    {
        public ValueTask<MessagingAuthorizationDecision> AuthorizeAsync(
            MessagingActor actor, MessagingPermission permission, ConversationId? conversationId = null,
            MessagingActor? target = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessagingAuthorizationDecision(true));
    }

    private sealed class InMemoryCallStore : IMessagingCallStore
    {
        private readonly ConcurrentDictionary<CallId, MessagingCallRecord> _calls = new();

        public ValueTask<MessagingCallRecord> CreateCallAsync(MessagingCallRecord call, CancellationToken cancellationToken = default)
        {
            _calls[call.CallId] = call;
            return ValueTask.FromResult(call);
        }

        public ValueTask<MessagingCallRecord?> GetCallAsync(TenantId tenantId, CallId callId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_calls.TryGetValue(callId, out var c) && c.TenantId == tenantId ? c : null);

        public ValueTask<IReadOnlyList<MessagingCallRecord>> GetActiveCallsForConversationAsync(TenantId tenantId, ConversationId conversationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<MessagingCallRecord>>(
                _calls.Values.Where(c => c.TenantId == tenantId && c.ConversationId == conversationId && !MessagingCallCoordinator.IsTerminalState(c.State)).ToArray());

        public ValueTask<bool> TryTransitionCallAsync(MessagingCallRecord updated, CallState expectedState, CancellationToken cancellationToken = default)
        {
            lock (_calls)
            {
                if (!_calls.TryGetValue(updated.CallId, out var cur) || cur.State != expectedState)
                    return ValueTask.FromResult(false);
                _calls[updated.CallId] = updated;
                return ValueTask.FromResult(true);
            }
        }
    }

    private sealed class InMemoryFollowUpStore : IFollowUpStore
    {
        private readonly ConcurrentDictionary<FollowUpId, FollowUpRecord> _items = new();
        private readonly ConcurrentDictionary<ContinuationId, ContinuationRecord> _continuations = new();

        public ValueTask<FollowUpRecord> CreateFollowUpAsync(FollowUpRecord followUp, CancellationToken cancellationToken = default)
        {
            _items[followUp.FollowUpId] = followUp;
            return ValueTask.FromResult(followUp);
        }

        public ValueTask<FollowUpRecord?> GetFollowUpAsync(TenantId tenantId, FollowUpId followUpId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.TryGetValue(followUpId, out var f) && f.TenantId == tenantId ? f : null);

        public ValueTask<IReadOnlyList<FollowUpRecord>> ClaimDueFollowUpsAsync(DateTimeOffset utcNow, string leaseOwner, TimeSpan leaseDuration, int maxBatchSize, CancellationToken cancellationToken = default)
        {
            var claimed = new List<FollowUpRecord>();
            lock (_items)
            {
                foreach (var item in _items.Values.Where(f => f.State == FollowUpState.Scheduled && f.DueAtUtc <= utcNow).Take(maxBatchSize).ToArray())
                {
                    var next = item with
                    {
                        State = FollowUpState.Claimed,
                        LeaseOwner = leaseOwner,
                        LeaseFence = item.LeaseFence + 1,
                        LeaseExpiresAtUtc = utcNow.Add(leaseDuration)
                    };
                    _items[item.FollowUpId] = next;
                    claimed.Add(next);
                }
            }
            return ValueTask.FromResult<IReadOnlyList<FollowUpRecord>>(claimed);
        }

        public ValueTask<bool> TryTransitionFollowUpAsync(FollowUpRecord updated, FollowUpState expectedState, long expectedLeaseFence, CancellationToken cancellationToken = default)
        {
            lock (_items)
            {
                if (!_items.TryGetValue(updated.FollowUpId, out var cur) || cur.State != expectedState || cur.LeaseFence != expectedLeaseFence)
                    return ValueTask.FromResult(false);
                _items[updated.FollowUpId] = updated;
                return ValueTask.FromResult(true);
            }
        }

        public ValueTask<IReadOnlyList<FollowUpRecord>> GetActiveFollowUpsForConversationAsync(TenantId tenantId, ConversationId conversationId, PrincipalId recipientPrincipalId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<FollowUpRecord>>(
                _items.Values.Where(f =>
                    f.TenantId == tenantId &&
                    f.ConversationId == conversationId &&
                    f.RecipientPrincipalId == recipientPrincipalId &&
                    f.State is FollowUpState.Scheduled or FollowUpState.Claimed or FollowUpState.Authorized or FollowUpState.Dispatched or FollowUpState.Waiting).ToArray());

        public ValueTask<ContinuationRecord> CreateContinuationAsync(ContinuationRecord continuation, CancellationToken cancellationToken = default)
        {
            _continuations[continuation.ContinuationId] = continuation;
            return ValueTask.FromResult(continuation);
        }
    }
}
