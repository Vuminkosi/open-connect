using System.Collections.Concurrent;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Messaging;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class MessagingConversationCoordinatorTests
{
    private readonly TenantId _tenantA = new(Ulid.NewUlid());
    private readonly TenantId _tenantB = new(Ulid.NewUlid());
    private readonly PrincipalId _human1 = new(Ulid.NewUlid());
    private readonly PrincipalId _human2 = new(Ulid.NewUlid());
    private readonly AgentId _agent1 = new("ops-agent-1");
    private readonly AgentId _agent2 = new("ops-agent-2");

    private MessagingActor Human(TenantId t, PrincipalId p, DeviceId? d = null) =>
        new(t, "crm-app", ParticipantKind.Human, PrincipalId: p, DeviceId: d ?? new DeviceId(Ulid.NewUlid()));

    private MessagingActor Agent(TenantId t, AgentId a) =>
        new(t, "crm-app", ParticipantKind.Agent, AgentId: a, DeviceId: new DeviceId(Ulid.NewUlid()));

    [Fact]
    public async Task A06_CreateConversation_AgentOnly_RejectedFailClosed()
    {
        var coordinator = CreateCoordinator(out _);
        var ex = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CreateConversationAsync(
                Agent(_tenantA, _agent1),
                ConversationSecurityProfile.AgentE2EE,
                [Agent(_tenantA, _agent2)]));

        Assert.Equal(ErrorCodes.HumanParticipantRequired, ex.ErrorCode);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task A06_PrivateE2EE_RejectsAgentOnCreateAndOnAdd()
    {
        var coordinator = CreateCoordinator(out _);
        var h1 = Human(_tenantA, _human1);
        var h2 = Human(_tenantA, _human2);
        var a1 = Agent(_tenantA, _agent1);

        var createEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CreateConversationAsync(h1, ConversationSecurityProfile.PrivateE2EE, [a1]));
        Assert.Equal(ErrorCodes.PrivateE2EEAgentForbidden, createEx.ErrorCode);

        var convo = await coordinator.CreateConversationAsync(h1, ConversationSecurityProfile.PrivateE2EE, [h2]);
        Assert.Equal(ConversationSecurityProfile.PrivateE2EE, convo.SecurityProfile);

        // Cannot silently convert PrivateE2EE by adding an agent
        var addEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.AddParticipantAsync(h1, convo.ConversationId, a1, ParticipantRole.OperationalAgent));
        Assert.Equal(ErrorCodes.PrivateE2EEAgentForbidden, addEx.ErrorCode);
        Assert.Equal(409, addEx.StatusCode);
    }

    [Fact]
    public async Task A06_LastHumanLeaving_ClosesConversationAndBlocksSubsequentSend()
    {
        var coordinator = CreateCoordinator(out _);
        var h1 = Human(_tenantA, _human1);
        var a1 = Agent(_tenantA, _agent1);

        var convo = await coordinator.CreateConversationAsync(h1, ConversationSecurityProfile.AgentE2EE, [a1]);
        var removal = await coordinator.RemoveParticipantAsync(h1, convo.ConversationId, ParticipantKind.Human, _human1, null);

        Assert.True(removal.ConversationClosed);
        Assert.True(removal.TerminateActiveCalls);
        Assert.Equal(ConversationState.Closed, removal.Conversation.State);

        var sendEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CommitMessageAsync(a1, convo.ConversationId, MakeEnvelope(removal.Conversation.CryptoEpoch, "Y2lwaGVy"), "op-after-close"));
        Assert.Equal(ErrorCodes.ConversationClosed, sendEx.ErrorCode);
        Assert.Equal(409, sendEx.StatusCode);
    }

    [Fact]
    public async Task A07_CrossTenantAccess_DeniedWithoutLeakage()
    {
        var coordinator = CreateCoordinator(out _);
        var hA = Human(_tenantA, _human1);
        var hB = Human(_tenantB, _human2);

        var convo = await coordinator.CreateConversationAsync(hA, ConversationSecurityProfile.PrivateE2EE, []);

        var sendEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CommitMessageAsync(hB, convo.ConversationId, MakeEnvelope(1, "Y2lwaGVy"), "cross-tenant-op"));
        Assert.Equal(ErrorCodes.ConversationNotFound, sendEx.ErrorCode);
        Assert.Equal(404, sendEx.StatusCode);
    }

    [Fact]
    public async Task A09_And_A12_IdempotentCommit_PayloadConflict_AndEpochMismatch()
    {
        var coordinator = CreateCoordinator(out _);
        var h1 = Human(_tenantA, _human1);
        var convo = await coordinator.CreateConversationAsync(h1, ConversationSecurityProfile.PrivateE2EE, []);

        // Stale/wrong epoch rejected
        var epochEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CommitMessageAsync(h1, convo.ConversationId, MakeEnvelope(99, "Y2lwaGVy"), "op-bad-epoch"));
        Assert.Equal(ErrorCodes.CryptoEpochMismatch, epochEx.ErrorCode);
        Assert.Equal(409, epochEx.StatusCode);

        // First commit succeeds
        var env1 = MakeEnvelope(1, "Y2lwaGVyMQ==");
        var first = await coordinator.CommitMessageAsync(h1, convo.ConversationId, env1, "op-1");
        Assert.False(first.WasDeduplicated);

        // Same ClientOperationId + identical payload deduplicates
        var dup = await coordinator.CommitMessageAsync(h1, convo.ConversationId, env1, "op-1");
        Assert.True(dup.WasDeduplicated);
        Assert.Equal(first.Message.MessageId, dup.Message.MessageId);

        // Same ClientOperationId + different payload -> 409 Conflict
        var conflictEx = await Assert.ThrowsAsync<MessagingDomainException>(async () =>
            await coordinator.CommitMessageAsync(h1, convo.ConversationId, MakeEnvelope(1, "Y2lwaGVyMg=="), "op-1"));
        Assert.Equal(ErrorCodes.Conflict, conflictEx.ErrorCode);
        Assert.Equal(409, conflictEx.StatusCode);
    }

    [Fact]
    public async Task A12_Receipts_AreStrictlyMonotonic_AndNeverRegress()
    {
        var coordinator = CreateCoordinator(out _);
        var h1 = Human(_tenantA, _human1);
        var convo = await coordinator.CreateConversationAsync(h1, ConversationSecurityProfile.PrivateE2EE, []);
        var msg = await coordinator.CommitMessageAsync(h1, convo.ConversationId, MakeEnvelope(1, "Y2lwaGVy"), "op-r1");

        var r1 = await coordinator.RecordReceiptAsync(h1, convo.ConversationId, msg.Message.MessageId, MessageReceiptKind.Committed);
        Assert.Equal(MessageReceiptKind.Committed, r1.Kind);

        var r2 = await coordinator.RecordReceiptAsync(h1, convo.ConversationId, msg.Message.MessageId, MessageReceiptKind.Read);
        Assert.Equal(MessageReceiptKind.Read, r2.Kind);

        // Delayed Delivered receipt after Read must NOT regress
        var r3 = await coordinator.RecordReceiptAsync(h1, convo.ConversationId, msg.Message.MessageId, MessageReceiptKind.Delivered);
        Assert.Equal(MessageReceiptKind.Read, r3.Kind);

        // Failed after Read must NOT regress
        var r4 = await coordinator.RecordReceiptAsync(h1, convo.ConversationId, msg.Message.MessageId, MessageReceiptKind.Failed, "late_error");
        Assert.Equal(MessageReceiptKind.Read, r4.Kind);
    }

    private static EncryptedMessageEnvelope MakeEnvelope(long epoch, string ciphertextBase64) =>
        new(MessagingCryptoEnvelopeService.CanonicalProtocolName, epoch, ciphertextBase64, "bm9uY2UxMjM0NTY=", "dGFnMTIzNDU2Nzg5MDEyMzQ1Ng==");

    private static ConversationCoordinator CreateCoordinator(out InMemoryConversationStore store)
    {
        store = new InMemoryConversationStore();
        return new ConversationCoordinator(store, new AllowAllAuthorizer(), new PassthroughResolver());
    }

    private sealed class AllowAllAuthorizer : IMessagingAuthorizer
    {
        public ValueTask<MessagingAuthorizationDecision> AuthorizeAsync(
            MessagingActor actor, MessagingPermission permission, ConversationId? conversationId = null,
            MessagingActor? target = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessagingAuthorizationDecision(true));
    }

    private sealed class PassthroughResolver : IMessagingRecipientResolver
    {
        public ValueTask<MessagingActor?> ResolveHumanAsync(TenantId tenantId, string applicationId, PrincipalId principalId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MessagingActor?>(new MessagingActor(tenantId, applicationId, ParticipantKind.Human, PrincipalId: principalId));

        public ValueTask<MessagingActor?> ResolveAgentAsync(TenantId tenantId, string applicationId, AgentId agentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MessagingActor?>(new MessagingActor(tenantId, applicationId, ParticipantKind.Agent, AgentId: agentId));
    }

    internal sealed class InMemoryConversationStore : IMessagingConversationStore
    {
        private readonly ConcurrentDictionary<(TenantId, ConversationId), ConversationRecord> _convos = new();
        private readonly ConcurrentDictionary<(TenantId, ConversationId, MessageId), MessageRecord> _messages = new();
        private readonly ConcurrentDictionary<(TenantId, ConversationId, string), MessageRecord> _byOp = new();
        private readonly ConcurrentDictionary<(TenantId, MessageId, DeviceId), MessageReceiptRecord> _receipts = new();

        public ValueTask<ConversationRecord> CreateConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
        {
            _convos[(conversation.TenantId, conversation.ConversationId)] = conversation;
            return ValueTask.FromResult(conversation);
        }

        public ValueTask<ConversationRecord?> GetConversationAsync(TenantId tenantId, ConversationId conversationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_convos.GetValueOrDefault((tenantId, conversationId)));

        public ValueTask<ConversationRecord> UpdateConversationAsync(ConversationRecord conversation, long expectedMembershipVersion, CancellationToken cancellationToken = default)
        {
            var key = (conversation.TenantId, conversation.ConversationId);
            if (!_convos.TryGetValue(key, out var existing) || existing.MembershipVersion != expectedMembershipVersion)
                throw new MessagingDomainException(ErrorCodes.Conflict, "Membership version mismatch.", 409);
            _convos[key] = conversation;
            return ValueTask.FromResult(conversation);
        }

        public ValueTask<MessageRecord?> GetMessageByClientOperationIdAsync(TenantId tenantId, ConversationId conversationId, string clientOperationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_byOp.GetValueOrDefault((tenantId, conversationId, clientOperationId)));

        public ValueTask<MessageRecord> CommitMessageAsync(MessageRecord message, CancellationToken cancellationToken = default)
        {
            var convoKey = (message.TenantId, message.ConversationId);
            if (_convos.TryGetValue(convoKey, out var c))
                _convos[convoKey] = c with { NextSequence = c.NextSequence + 1 };
            _messages[(message.TenantId, message.ConversationId, message.MessageId)] = message;
            _byOp[(message.TenantId, message.ConversationId, message.ClientOperationId)] = message;
            return ValueTask.FromResult(message);
        }

        public ValueTask<MessageRecord?> GetMessageAsync(TenantId tenantId, ConversationId conversationId, MessageId messageId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_messages.GetValueOrDefault((tenantId, conversationId, messageId)));

        public ValueTask<MessageReceiptRecord?> GetReceiptAsync(TenantId tenantId, MessageId messageId, DeviceId deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_receipts.GetValueOrDefault((tenantId, messageId, deviceId)));

        public ValueTask<MessageReceiptRecord> UpsertReceiptAsync(MessageReceiptRecord receipt, CancellationToken cancellationToken = default)
        {
            _receipts[(receipt.TenantId, receipt.MessageId, receipt.DeviceId)] = receipt;
            return ValueTask.FromResult(receipt);
        }
    }
}
