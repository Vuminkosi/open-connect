using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Hosting.AspNetCore.Messaging;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public sealed class MessagingFollowUpSupervisorTests
{
    [Fact]
    public async Task ExecuteSingleCycleAsync_DoesNothingWhenDisabled()
    {
        var coordinator = new FakeFollowUpCoordinator([]);
        var dispatcher = new FakeDispatcher();
        var options = Options.Create(new MessagingHostingOptions { Enabled = false });
        var supervisor = new MessagingFollowUpSupervisorService(coordinator, dispatcher, options, NullLogger<MessagingFollowUpSupervisorService>.Instance);

        var processed = await supervisor.ExecuteSingleCycleAsync(CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Equal(0, coordinator.ClaimCalls);
    }

    [Fact]
    public async Task ExecuteSingleCycleAsync_ClaimsBoundedBatchAndDispatchesWithLeaseFence()
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var item1 = CreateFollowUp(tenant, leaseFence: 7);
        var item2 = CreateFollowUp(tenant, leaseFence: 9);

        var coordinator = new FakeFollowUpCoordinator([item1, item2]) { ThrowOnSecond = true };
        var dispatcher = new FakeDispatcher();
        var options = Options.Create(new MessagingHostingOptions
        {
            Enabled = true,
            FollowUpBatchSize = 10,
            FollowUpLeaseOwner = "gw-test-1",
            FollowUpLeaseDuration = TimeSpan.FromSeconds(45)
        });

        var supervisor = new MessagingFollowUpSupervisorService(coordinator, dispatcher, options, NullLogger<MessagingFollowUpSupervisorService>.Instance);

        var processed = await supervisor.ExecuteSingleCycleAsync(CancellationToken.None);

        // Item 1 succeeds, Item 2 throws domain exception (e.g. quiet hours) and is safely isolated
        Assert.Equal(1, processed);
        Assert.Equal(1, coordinator.ClaimCalls);
        Assert.Equal("gw-test-1", coordinator.LastLeaseOwner);
        Assert.Equal(10, coordinator.LastBatchSize);
        Assert.Single(dispatcher.DispatchedIds);
        Assert.Equal(item1.FollowUpId, dispatcher.DispatchedIds[0]);
    }

    private static FollowUpRecord CreateFollowUp(TenantId tenant, long leaseFence) =>
        new(
            tenant,
            "app",
            new FollowUpId(Ulid.NewUlid()),
            new ConversationId(Ulid.NewUlid()),
            null,
            new AgentId("ops-agent"),
            new PrincipalId(Ulid.NewUlid()),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            1,
            3,
            FollowUpState.Claimed,
            $"idem-{Guid.NewGuid():N}",
            "causal-1",
            "gw-test-1",
            leaseFence,
            DateTimeOffset.UtcNow.AddSeconds(30));

    private sealed class FakeDispatcher : IMessagingFollowUpDispatcher
    {
        public List<FollowUpId> DispatchedIds { get; } = [];

        public ValueTask<bool> DispatchAsync(FollowUpRecord followUp, CancellationToken cancellationToken = default)
        {
            DispatchedIds.Add(followUp.FollowUpId);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FakeFollowUpCoordinator(IReadOnlyList<FollowUpRecord> batch) : IFollowUpSchedulerCoordinator
    {
        public int ClaimCalls { get; private set; }
        public string? LastLeaseOwner { get; private set; }
        public int LastBatchSize { get; private set; }
        public bool ThrowOnSecond { get; set; }
        private int _dispatchIndex;

        public ValueTask<IReadOnlyList<FollowUpRecord>> ClaimDueBatchAsync(string leaseOwner, TimeSpan leaseDuration, int maxBatchSize, CancellationToken cancellationToken = default)
        {
            ClaimCalls++;
            LastLeaseOwner = leaseOwner;
            LastBatchSize = maxBatchSize;
            return ValueTask.FromResult(batch);
        }

        public async ValueTask<FollowUpRecord> AuthorizeAndDispatchAsync(TenantId tenantId, FollowUpId followUpId, string leaseOwner, long leaseFence, Func<FollowUpRecord, CancellationToken, ValueTask<bool>> dispatchCallback, CancellationToken cancellationToken = default)
        {
            var idx = _dispatchIndex++;
            if (ThrowOnSecond && idx == 1)
                throw new MessagingDomainException(ErrorCodes.FollowUpQuietHours, "Quiet hours active.", 409);

            var item = batch.First(b => b.FollowUpId == followUpId);
            Assert.Equal(item.LeaseFence, leaseFence);
            await dispatchCallback(item, cancellationToken).ConfigureAwait(false);
            return item with { State = FollowUpState.Dispatched };
        }

        public ValueTask<FollowUpRecord> ScheduleFollowUpAsync(MessagingActor agentActor, ConversationId conversationId, PrincipalId recipientPrincipalId, DateTimeOffset dueAtUtc, int maxAttempts, string idempotencyKey, string causalEventId, TaskId? taskId = null, TimeOnly? quietHoursStartUtc = null, TimeOnly? quietHoursEndUtc = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<FollowUpRaceResolution> RecordHumanReplyAsync(TenantId tenantId, ConversationId conversationId, PrincipalId humanPrincipalId, string triggeringEventId, SessionId? sessionId = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<FollowUpRecord> CancelFollowUpAsync(TenantId tenantId, FollowUpId followUpId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
