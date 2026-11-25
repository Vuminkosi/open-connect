using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Messaging;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class MessagingPersistenceTests
{
    private static readonly TenantId TenantA = new(Ulid.NewUlid());
    private static readonly TenantId TenantB = new(Ulid.NewUlid());
    private const string AppId = "insurance-crm";

    private static async Task<(SqliteConnection Connection, OpenConnectDbContext Context)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
            .Options;
        var db = new OpenConnectDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    private static ConversationRecord MakeConversation(
        TenantId tenantId,
        ConversationSecurityProfile profile,
        params ConversationParticipant[] participants)
    {
        var convId = new ConversationId(Ulid.NewUlid());
        var fixedParticipants = participants.Select(p => p with { ConversationId = convId }).ToArray();
        return new ConversationRecord(
            tenantId,
            AppId,
            convId,
            profile,
            ConversationState.Active,
            MembershipVersion: 1,
            CryptoEpoch: 1,
            NextSequence: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Participants: fixedParticipants);
    }

    private static ConversationParticipant Human(PrincipalId principalId, ParticipantRole role = ParticipantRole.Owner, ParticipantStatus status = ParticipantStatus.Active) =>
        new(default, ParticipantKind.Human, principalId, null, role, status, JoinVersion: 1, JoinedAt: DateTimeOffset.UtcNow);

    private static ConversationParticipant Agent(string agentId, ParticipantStatus status = ParticipantStatus.Active) =>
        new(default, ParticipantKind.Agent, null, new AgentId(agentId), ParticipantRole.OperationalAgent, status, JoinVersion: 1, JoinedAt: DateTimeOffset.UtcNow);

    private static EncryptedMessageEnvelope Envelope(string ciphertext = "Y2lwaGVydGV4dA==") =>
        new("mls-1.0", 1, ciphertext, "bm9uY2U=", "dGFn");

    [Fact]
    public async Task CreateConversation_rejects_agent_only_and_private_e2ee_with_agent()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var store = new EfMessagingConversationStore<OpenConnectDbContext>(db);

        var agentOnly = MakeConversation(TenantA, ConversationSecurityProfile.AgentE2EE, Agent("ops-agent"));
        var ex1 = await Assert.ThrowsAsync<MessagingDomainException>(() => store.CreateConversationAsync(agentOnly).AsTask());
        Assert.Equal("invalid_request", ex1.ErrorCode);

        var privateWithAgent = MakeConversation(TenantA, ConversationSecurityProfile.PrivateE2EE, Human(new PrincipalId(Ulid.NewUlid())), Agent("ops-agent"));
        var ex2 = await Assert.ThrowsAsync<MessagingDomainException>(() => store.CreateConversationAsync(privateWithAgent).AsTask());
        Assert.Equal("invalid_request", ex2.ErrorCode);
    }

    [Fact]
    public async Task Removing_last_human_closes_conversation_ends_active_calls_and_blocks_subsequent_messages()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var convStore = new EfMessagingConversationStore<OpenConnectDbContext>(db);
        var callStore = new EfMessagingCallStore<OpenConnectDbContext>(db);

        var humanId = new PrincipalId(Ulid.NewUlid());
        var created = await convStore.CreateConversationAsync(
            MakeConversation(TenantA, ConversationSecurityProfile.AgentE2EE, Human(humanId), Agent("claims-agent")));

        var call = await callStore.CreateCallAsync(new MessagingCallRecord(
            TenantA,
            AppId,
            new CallId(Ulid.NewUlid()),
            created.ConversationId,
            ParticipantKind.Agent,
            null,
            new AgentId("claims-agent"),
            [humanId],
            CallState.Ringing,
            CallMediaSecurityMode.EndpointE2EE,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1)));

        // Remove the last human
        var updatedParticipants = created.Participants
            .Select(p => p.Kind == ParticipantKind.Human ? p with { Status = ParticipantStatus.Left, LeaveVersion = 2 } : p)
            .ToArray();

        var updated = await convStore.UpdateConversationAsync(
            created with { Participants = updatedParticipants, MembershipVersion = 2 },
            expectedMembershipVersion: 1);

        Assert.Equal(ConversationState.Closed, updated.State);

        var activeCalls = await callStore.GetActiveCallsForConversationAsync(TenantA, created.ConversationId);
        Assert.Empty(activeCalls);

        var endedCall = await callStore.GetCallAsync(TenantA, call.CallId);
        Assert.NotNull(endedCall);
        Assert.Equal(CallState.Ended, endedCall.State);
        Assert.Equal("last_human_removed", endedCall.EndReason);

        // Attempting to commit a message on the closed conversation must fail
        var msg = new MessageRecord(
            TenantA,
            created.ConversationId,
            new MessageId(Ulid.NewUlid()),
            0,
            ParticipantKind.Agent,
            null,
            new AgentId("claims-agent"),
            null,
            Envelope(),
            "op-after-close",
            "digest-1",
            DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<MessagingDomainException>(() => convStore.CommitMessageAsync(msg).AsTask());
        Assert.Equal("conflict", ex.ErrorCode);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public async Task UpdateConversation_enforces_immutable_profile_and_membership_version_cas()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var store = new EfMessagingConversationStore<OpenConnectDbContext>(db);
        var humanId = new PrincipalId(Ulid.NewUlid());
        var created = await store.CreateConversationAsync(
            MakeConversation(TenantA, ConversationSecurityProfile.PrivateE2EE, Human(humanId)));

        var profileChangeEx = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            store.UpdateConversationAsync(created with { SecurityProfile = ConversationSecurityProfile.AgentE2EE }, 1).AsTask());
        Assert.Equal("conflict", profileChangeEx.ErrorCode);

        var staleVersionEx = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            store.UpdateConversationAsync(created with { MembershipVersion = 2 }, expectedMembershipVersion: 99).AsTask());
        Assert.Equal("conflict", staleVersionEx.ErrorCode);
    }

    [Fact]
    public async Task CommitMessage_allocates_monotonic_sequence_deduplicates_idempotently_detects_payload_conflict_and_writes_outbox_atomically()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var store = new EfMessagingConversationStore<OpenConnectDbContext>(db);
        var humanId = new PrincipalId(Ulid.NewUlid());
        var deviceId = new DeviceId(Ulid.NewUlid());
        var conv = await store.CreateConversationAsync(
            MakeConversation(TenantA, ConversationSecurityProfile.PrivateE2EE, Human(humanId)));

        var msg1 = new MessageRecord(
            TenantA,
            conv.ConversationId,
            new MessageId(Ulid.NewUlid()),
            0,
            ParticipantKind.Human,
            humanId,
            null,
            deviceId,
            Envelope("Y3Qx"),
            "op-1",
            "sha256-aaa",
            DateTimeOffset.UtcNow);

        var committed1 = await store.CommitMessageAsync(msg1);
        Assert.Equal(1, committed1.Sequence);

        // Idempotent retry with same ClientOperationId and same digest returns original
        var retry = await store.CommitMessageAsync(msg1 with { MessageId = new MessageId(Ulid.NewUlid()) });
        Assert.Equal(committed1.MessageId, retry.MessageId);
        Assert.Equal(1, retry.Sequence);

        // Same ClientOperationId with DIFFERENT payload digest must fail with 409 Conflict
        var mismatch = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            store.CommitMessageAsync(msg1 with { MessageId = new MessageId(Ulid.NewUlid()), PayloadDigestSha256 = "sha256-DIFFERENT" }).AsTask());
        Assert.Equal("conflict", mismatch.ErrorCode);
        Assert.Equal(409, mismatch.StatusCode);

        // Second distinct message gets sequence 2
        var committed2 = await store.CommitMessageAsync(msg1 with { MessageId = new MessageId(Ulid.NewUlid()), ClientOperationId = "op-2", PayloadDigestSha256 = "sha256-bbb" });
        Assert.Equal(2, committed2.Sequence);

        // Verify outbox rows committed atomically (exactly 2 for the 2 distinct messages)
        var outboxRows = await db.Set<OutboxEntity>().AsNoTracking().OrderBy(o => o.CreatedUtc).ToListAsync();
        Assert.Equal(2, outboxRows.Count);
        Assert.All(outboxRows, row =>
        {
            Assert.Equal(TenantA.Value.ToString(), row.TenantScope);
            Assert.Equal($"oc.v1.messaging.durable.{TenantA.Value}.{conv.ConversationId.Value}", row.Subject);
            Assert.Equal("pending", row.Status);
        });

        // Verify rollback atomicity: rolled-back transaction leaves neither message nor outbox row
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await store.CommitMessageAsync(msg1 with { MessageId = new MessageId(Ulid.NewUlid()), ClientOperationId = "op-rollback", PayloadDigestSha256 = "sha256-ccc" });
            await tx.RollbackAsync();
        }

        Assert.Null(await store.GetMessageByClientOperationIdAsync(TenantA, conv.ConversationId, "op-rollback"));
        Assert.Equal(2, await db.Set<OutboxEntity>().AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task UpsertReceipt_is_monotonic_and_read_implies_delivered()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var store = new EfMessagingConversationStore<OpenConnectDbContext>(db);
        var humanId = new PrincipalId(Ulid.NewUlid());
        var deviceId = new DeviceId(Ulid.NewUlid());
        var conv = await store.CreateConversationAsync(
            MakeConversation(TenantA, ConversationSecurityProfile.PrivateE2EE, Human(humanId)));

        var msg = await store.CommitMessageAsync(new MessageRecord(
            TenantA, conv.ConversationId, new MessageId(Ulid.NewUlid()), 0,
            ParticipantKind.Human, humanId, null, deviceId, Envelope(), "op-rcpt", "digest-rcpt", DateTimeOffset.UtcNow));

        var now = DateTimeOffset.UtcNow;
        var r1 = await store.UpsertReceiptAsync(new MessageReceiptRecord(TenantA, conv.ConversationId, msg.MessageId, humanId, deviceId, MessageReceiptKind.Queued, 1, now));
        Assert.Equal(MessageReceiptKind.Queued, r1.Kind);

        var r2 = await store.UpsertReceiptAsync(new MessageReceiptRecord(TenantA, conv.ConversationId, msg.MessageId, humanId, deviceId, MessageReceiptKind.Read, 1, now.AddSeconds(2)));
        Assert.Equal(MessageReceiptKind.Read, r2.Kind);

        // Verify Read implies Delivered in persisted entity
        var entity = await db.MessagingReceipts.AsNoTracking().SingleAsync();
        Assert.NotNull(entity.DeliveredAtUtc);
        Assert.NotNull(entity.ReadAtUtc);

        // Attempting to downgrade to Delivered or Failed must NOT downgrade
        var downgradeDelivered = await store.UpsertReceiptAsync(new MessageReceiptRecord(TenantA, conv.ConversationId, msg.MessageId, humanId, deviceId, MessageReceiptKind.Delivered, 1, now.AddSeconds(5)));
        Assert.Equal(MessageReceiptKind.Read, downgradeDelivered.Kind);

        var downgradeFailed = await store.UpsertReceiptAsync(new MessageReceiptRecord(TenantA, conv.ConversationId, msg.MessageId, humanId, deviceId, MessageReceiptKind.Failed, 1, now.AddSeconds(6), "late_failure"));
        Assert.Equal(MessageReceiptKind.Read, downgradeFailed.Kind);
    }

    [Fact]
    public async Task ReplayMessages_enforces_tenant_isolation_and_active_participant_membership()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var store = new EfMessagingConversationStore<OpenConnectDbContext>(db);
        var member = new PrincipalId(Ulid.NewUlid());
        var outsider = new PrincipalId(Ulid.NewUlid());
        var conv = await store.CreateConversationAsync(
            MakeConversation(TenantA, ConversationSecurityProfile.PrivateE2EE, Human(member)));

        for (var i = 1; i <= 3; i++)
        {
            await store.CommitMessageAsync(new MessageRecord(
                TenantA, conv.ConversationId, new MessageId(Ulid.NewUlid()), 0,
                ParticipantKind.Human, member, null, null, Envelope(), $"op-{i}", $"digest-{i}", DateTimeOffset.UtcNow));
        }

        // Cross-tenant lookup returns null
        Assert.Null(await store.GetConversationAsync(TenantB, conv.ConversationId));

        // Cross-tenant replay fails closed with 403 unauthorized
        var crossTenantEx = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            store.ReplayMessagesAsync(TenantB, conv.ConversationId, ParticipantKind.Human, member, null, 0, 10).AsTask());
        Assert.Equal("unauthorized", crossTenantEx.ErrorCode);

        // Non-participant replay fails closed with 403 unauthorized
        var outsiderEx = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            store.ReplayMessagesAsync(TenantA, conv.ConversationId, ParticipantKind.Human, outsider, null, 0, 10).AsTask());
        Assert.Equal("unauthorized", outsiderEx.ErrorCode);

        // Authorized participant gets cursor-paginated ordered results
        var page1 = await store.ReplayMessagesAsync(TenantA, conv.ConversationId, ParticipantKind.Human, member, null, afterSequence: 0, limit: 2);
        Assert.Equal([1L, 2L], page1.Select(m => m.Sequence).ToArray());

        var page2 = await store.ReplayMessagesAsync(TenantA, conv.ConversationId, ParticipantKind.Human, member, null, afterSequence: 2, limit: 2);
        Assert.Equal([3L], page2.Select(m => m.Sequence).ToArray());
    }

    [Fact]
    public async Task Enrollment_cas_key_packages_consume_once_and_refresh_family_revocation()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var store = new EfDeviceEnrollmentStore<OpenConnectDbContext>(db);
        var principal = new PrincipalId(Ulid.NewUlid());
        var device1 = new DeviceId(Ulid.NewUlid());
        var device2 = new DeviceId(Ulid.NewUlid());
        var now = DateTimeOffset.UtcNow;

        // 1. Enrollment single-winner CAS claim
        var enrollment = await store.CreateEnrollmentAsync(new DeviceEnrollmentRecord(
            TenantA, AppId, new EnrollmentId(Ulid.NewUlid()), principal,
            "https://idp.example", "sub-1", "hash-token-1", "123-456",
            EnrollmentState.Pending, now, now.AddMinutes(2)));

        var winClaim = await store.TryUpdateEnrollmentAsync(
            enrollment with { State = EnrollmentState.Claimed, ClaimedDeviceId = device1, ClaimedAt = now },
            EnrollmentState.Pending);
        var loseClaim = await store.TryUpdateEnrollmentAsync(
            enrollment with { State = EnrollmentState.Claimed, ClaimedDeviceId = device2, ClaimedAt = now },
            EnrollmentState.Pending);

        Assert.True(winClaim);
        Assert.False(loseClaim);

        // Cross-tenant enrollment lookup returns null
        Assert.Null(await store.GetEnrollmentAsync(TenantB, enrollment.EnrollmentId));

        // 2. Consume-once key package allocation
        var kp = new DeviceKeyPackage(
            TenantA, new KeyPackageId(Ulid.NewUlid()), principal, null, device1,
            "mls-1.0", "cHVibGljLXBrZw==", now, now.AddHours(1));
        await store.PublishKeyPackageAsync(kp, AppId);

        var expiredKp = new DeviceKeyPackage(
            TenantA, new KeyPackageId(Ulid.NewUlid()), principal, null, device1,
            "mls-1.0", "ZXhwaXJlZA==", now.AddHours(-2), now.AddHours(-1));
        await store.PublishKeyPackageAsync(expiredKp, AppId);

        var consumed = await store.TryConsumeKeyPackageAsync(TenantA, AppId, device1, now);
        Assert.NotNull(consumed);
        Assert.Equal(kp.KeyPackageId, consumed.KeyPackageId);
        Assert.True(consumed.Consumed);

        // Second consume returns null (expired one ignored, first one already consumed)
        Assert.Null(await store.TryConsumeKeyPackageAsync(TenantA, AppId, device1, now));

        // 3. Refresh grant rotation CAS & reuse family revocation
        var grant1 = await store.CreateRefreshGrantAsync(new RefreshGrantRecord(
            TenantA, AppId, new RefreshGrantId(Ulid.NewUlid()), "fam-1", device1, principal,
            "https://idp.example", "sub-1", "mobile", "rt-hash-1", RefreshGrantStatus.Active, now, now.AddDays(7)));
        var grant2 = await store.CreateRefreshGrantAsync(new RefreshGrantRecord(
            TenantA, AppId, new RefreshGrantId(Ulid.NewUlid()), "fam-1", device1, principal,
            "https://idp.example", "sub-1", "mobile", "rt-hash-2", RefreshGrantStatus.Active, now, now.AddDays(7)));
        var otherTenantGrant = await store.CreateRefreshGrantAsync(new RefreshGrantRecord(
            TenantB, AppId, new RefreshGrantId(Ulid.NewUlid()), "fam-1", device2, principal,
            "https://idp.example", "sub-1", "mobile", "rt-hash-other", RefreshGrantStatus.Active, now, now.AddDays(7)));

        Assert.True(await store.TryUpdateRefreshGrantAsync(grant1 with { Status = RefreshGrantStatus.Rotated, RotatedAt = now }, RefreshGrantStatus.Active));
        Assert.False(await store.TryUpdateRefreshGrantAsync(grant1 with { Status = RefreshGrantStatus.Rotated, RotatedAt = now }, RefreshGrantStatus.Active));

        // Reuse detected -> revoke entire family in TenantA only
        await store.RevokeRefreshFamilyAsync(TenantA, "fam-1", now.AddSeconds(10));
        Assert.Equal(RefreshGrantStatus.Revoked, (await store.GetRefreshGrantByTokenHashAsync("rt-hash-1"))!.Status);
        Assert.Equal(RefreshGrantStatus.Revoked, (await store.GetRefreshGrantByTokenHashAsync("rt-hash-2"))!.Status);
        Assert.Equal(RefreshGrantStatus.Active, (await store.GetRefreshGrantByTokenHashAsync("rt-hash-other"))!.Status);

        // Push registration revocation
        await store.UpsertPushRegistrationAsync(new PushRegistrationRecord(
            TenantA, AppId, new PushRegistrationId(Ulid.NewUlid()), principal, device1,
            PushProviderKind.Fcm, "protected-handle", true, PushRegistrationStatus.Active, now));
        await store.RevokeDevicePushRegistrationsAsync(TenantA, device1, now.AddSeconds(20));
        var pushRow = await db.MessagingPushRegistrations.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(PushRegistrationStatus.Revoked), pushRow.Status);
        Assert.False(pushRow.ConsentGranted);
    }

    [Fact]
    public async Task FollowUp_due_batch_claim_lease_fencing_and_atomic_human_reply_race_resolution()
    {
        var (conn, db) = await CreateDbAsync();
        await using var _ = conn;
        await using var __ = db;
        var convStore = new EfMessagingConversationStore<OpenConnectDbContext>(db);
        var followUpStore = new EfFollowUpStore<OpenConnectDbContext>(db);

        var humanId = new PrincipalId(Ulid.NewUlid());
        var conv = await convStore.CreateConversationAsync(
            MakeConversation(TenantA, ConversationSecurityProfile.AgentE2EE, Human(humanId), Agent("ops-agent")));

        var now = DateTimeOffset.UtcNow;
        var dueFollowUp = await followUpStore.CreateFollowUpAsync(new FollowUpRecord(
            TenantA, AppId, new FollowUpId(Ulid.NewUlid()), conv.ConversationId,
            new TaskId(Ulid.NewUlid()), new AgentId("ops-agent"), humanId,
            DueAtUtc: now.AddMinutes(-1), Attempt: 0, MaxAttempts: 3, FollowUpState.Scheduled,
            IdempotencyKey: "fu-key-1", CausalEventId: "evt-1"));

        // Future follow-up should NOT be claimed yet
        await followUpStore.CreateFollowUpAsync(new FollowUpRecord(
            TenantA, AppId, new FollowUpId(Ulid.NewUlid()), conv.ConversationId,
            null, new AgentId("ops-agent"), humanId,
            DueAtUtc: now.AddHours(1), Attempt: 0, MaxAttempts: 3, FollowUpState.Scheduled,
            IdempotencyKey: "fu-key-future", CausalEventId: "evt-2"));

        // Claim due batch
        var claimed = await followUpStore.ClaimDueFollowUpsAsync(now, "worker-1", TimeSpan.FromSeconds(30), 10);
        var item = Assert.Single(claimed);
        Assert.Equal(dueFollowUp.FollowUpId, item.FollowUpId);
        Assert.Equal(FollowUpState.Claimed, item.State);
        Assert.Equal(1, item.LeaseFence);

        // Second worker cannot claim while lease is active
        Assert.Empty(await followUpStore.ClaimDueFollowUpsAsync(now.AddSeconds(5), "worker-2", TimeSpan.FromSeconds(30), 10));

        // Stale fence transition must fail
        var staleOk = await followUpStore.TryTransitionFollowUpAsync(
            item with { State = FollowUpState.Dispatched, LeaseFence = 2 },
            FollowUpState.Claimed,
            expectedLeaseFence: 0);
        Assert.False(staleOk);

        // Atomic human reply race wins with current fence and queues continuation
        var continuation = new ContinuationRecord(
            TenantA, new ContinuationId(Ulid.NewUlid()), conv.ConversationId,
            "human-reply-evt-1", new AgentId("ops-agent"), humanId,
            dueFollowUp.TaskId, null, ContinuationStatus.Queued, "exec-idem-1", CreatedAt: now);

        var race = await followUpStore.TryResolveHumanReplyRaceAsync(
            TenantA, item.FollowUpId, FollowUpState.Claimed, item.LeaseFence, continuation, now.AddSeconds(2));

        Assert.NotNull(race);
        Assert.True(race.SuppressedByHumanReply);
        Assert.Equal(FollowUpState.ResponseReceived, race.FollowUp.State);
        Assert.NotNull(race.QueuedContinuation);

        // Now worker-1 attempting to dispatch with its old fence (1) is blocked by CAS!
        var lateDispatch = await followUpStore.TryTransitionFollowUpAsync(
            item with { State = FollowUpState.Dispatched, LeaseFence = 2 },
            FollowUpState.Claimed,
            expectedLeaseFence: 1);
        Assert.False(lateDispatch);
    }

    [Fact]
    public void AddOpenConnectMessagingPersistence_registers_all_four_stores()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(o => o.UseSqlite("Data Source=:memory:"));
        services.AddOpenConnectMessagingPersistence();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMessagingConversationStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDeviceEnrollmentStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMessagingCallStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IFollowUpStore>());
    }
}
