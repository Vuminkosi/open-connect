using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Tools.Messaging;
using Xunit;

namespace OpenConnect.Tools.Tests;

public sealed class MessagingToolSuiteTests
{
    private readonly TenantId _tenant = new(Ulid.NewUlid());
    private readonly PrincipalId _human = new(Ulid.NewUlid());
    private readonly AgentId _agent = new("ops-agent");
    private readonly TaskId _task = new(Ulid.NewUlid());

    [Fact]
    public void GetAllToolMetadata_ContainsAllElevenCanonicalTools()
    {
        var all = MessagingToolSuite.GetAllToolMetadata();
        Assert.Equal(11, all.Count);
        Assert.Equal(MessagingToolIds.All.OrderBy(x => x), all.Select(m => m.ToolId.Value).OrderBy(x => x));
    }

    [Fact]
    public async Task ToolBroker_DeniesAllMessagingToolsWhenDisabledOrMissingPermission()
    {
        var options = new MessagingToolPolicyOptions { Enabled = false };
        var (broker, _) = CreateHarness(options);

        var req = new ToolAccessRequest(_agent, new ToolId(MessagingToolIds.ResolveRecipient), _human, _tenant, _task);
        var args = JsonSerializer.SerializeToElement(new { principal_id = _human.Value.ToString() });

        var ex = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(req, args));
        Assert.Equal(ErrorCodes.ToolNotAllowed, ex.ErrorCode);

        // Enable globally, still denied because agent has no MessagingPermission.Discover
        options.Enabled = true;
        var ex2 = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(req, args));
        Assert.Equal(ErrorCodes.ToolNotAllowed, ex2.ErrorCode);
    }

    [Fact]
    public async Task MessagingSend_BlocksAgentOnlyConversationAndPrivateE2EEAndCausalLoop()
    {
        var options = new MessagingToolPolicyOptions { Enabled = true, MaxCausalDepth = 2 };
        options.GrantPermissions(_agent, MessagingPermission.Send);
        var (broker, state) = CreateHarness(options);

        // 1. Agent-only conversation -> HumanParticipantRequired
        var agentOnlyConv = new ConversationId(Ulid.NewUlid());
        state.Conversations[agentOnlyConv] = new ConversationRecord(
            _tenant, "openconnect", agentOnlyConv, ConversationSecurityProfile.AgentE2EE, ConversationState.Active, 1, 1, 1, DateTimeOffset.UtcNow,
            [new ConversationParticipant(agentOnlyConv, ParticipantKind.Agent, null, _agent, ParticipantRole.OperationalAgent, ParticipantStatus.Active, 1)]);

        var req = new ToolAccessRequest(_agent, new ToolId(MessagingToolIds.Send), _human, _tenant, _task);
        var args = JsonSerializer.SerializeToElement(new { conversation_id = agentOnlyConv.Value.ToString(), text = "hello", client_operation_id = "op-1" });
        var exHuman = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(req, args));
        Assert.Equal(ErrorCodes.HumanParticipantRequired, exHuman.ErrorCode);

        // 2. PrivateE2EE conversation -> PrivateE2EEAgentForbidden
        var privateConv = new ConversationId(Ulid.NewUlid());
        state.Conversations[privateConv] = new ConversationRecord(
            _tenant, "openconnect", privateConv, ConversationSecurityProfile.PrivateE2EE, ConversationState.Active, 1, 1, 1, DateTimeOffset.UtcNow,
            [
                new ConversationParticipant(privateConv, ParticipantKind.Human, _human, null, ParticipantRole.Owner, ParticipantStatus.Active, 1),
                new ConversationParticipant(privateConv, ParticipantKind.Agent, null, _agent, ParticipantRole.OperationalAgent, ParticipantStatus.Active, 1)
            ]);
        var argsPriv = JsonSerializer.SerializeToElement(new { conversation_id = privateConv.Value.ToString(), text = "hello", client_operation_id = "op-2" });
        var exPriv = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(req, argsPriv));
        Assert.Equal(ErrorCodes.PrivateE2EEAgentForbidden, exPriv.ErrorCode);

        // 3. Causal depth exceeded -> ToolNotAllowed (prevents agent loops)
        var validConv = AddValidAgentConversation(state);
        var argsValid = JsonSerializer.SerializeToElement(new { conversation_id = validConv.Value.ToString(), text = "hello", client_operation_id = "op-3" });
        using (MessagingToolExecutionContextScope.Push(new MessagingToolExecutionContext(_tenant, _human, _agent, _task, CausalDepth: 3)))
        {
            var exLoop = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(req, argsValid));
            Assert.Equal(ErrorCodes.ToolNotAllowed, exLoop.ErrorCode);
        }
    }

    [Fact]
    public async Task MessagingStartCall_RequiresApprovalAndEnforcesE2EEMediaGate()
    {
        var options = new MessagingToolPolicyOptions { Enabled = true, RequireEncryption = true, AllowUnencryptedFallback = false };
        options.GrantPermissions(_agent, MessagingPermission.Call);
        var (broker, state) = CreateHarness(options);
        var convId = AddValidAgentConversation(state);

        var req = new ToolAccessRequest(_agent, new ToolId(MessagingToolIds.StartCall), _human, _tenant, _task);
        var argsTransportOnly = JsonSerializer.SerializeToElement(new { conversation_id = convId.Value.ToString(), media_security_mode = "transport-only" });

        // Missing ToolApproval -> ApprovalRequired
        var exNoApproval = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(req, argsTransportOnly));
        Assert.Equal(ErrorCodes.ApprovalRequired, exNoApproval.ErrorCode);

        // With valid ToolApproval, transport-only is still blocked by E2EE media gate
        var approval = state.AuthService.CreateApproval(req, argsTransportOnly, TimeSpan.FromMinutes(5));
        var exMedia = await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(req, argsTransportOnly, approval));
        Assert.Equal(ErrorCodes.MediaEncryptionRequired, exMedia.ErrorCode);
    }

    [Fact]
    public async Task MessagingRequestApproval_ProducesVerifiedSignedDescriptorAndRejectsTampering()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var options = new MessagingToolPolicyOptions { Enabled = true, ApprovalSigningKey = key };
        options.GrantPermissions(_agent, MessagingPermission.Approval);
        var (broker, state) = CreateHarness(options);
        var convId = AddValidAgentConversation(state);

        var req = new ToolAccessRequest(_agent, new ToolId(MessagingToolIds.RequestApproval), _human, _tenant, _task);
        var args = JsonSerializer.SerializeToElement(new
        {
            conversation_id = convId.Value.ToString(),
            target_human_principal_id = _human.Value.ToString(),
            action_type = "wire_transfer",
            action_summary = "Approve invoice #42",
            parameters_json = """{"amount":500}"""
        });

        var result = await broker.InvokeAsync(req, args);
        Assert.True(result.GetProperty("requires_explicit_human_confirmation").GetBoolean());
        Assert.NotNull(state.LastSavedApproval);

        var verifier = new MessagingApprovalDescriptorSigner(key);
        Assert.True(verifier.Verify(state.LastSavedApproval!, DateTimeOffset.UtcNow));

        // Tampered action type fails verification
        var tampered = state.LastSavedApproval! with { ActionType = "delete_database" };
        Assert.False(verifier.Verify(tampered, DateTimeOffset.UtcNow));

        // Expired descriptor fails verification
        Assert.False(verifier.Verify(state.LastSavedApproval!, state.LastSavedApproval!.ExpiresAtUtc.AddSeconds(1)));
    }

    [Fact]
    public async Task MessagingSendAndStatusAndResolveRecipient_SucceedWithEncryptedCommit()
    {
        var options = new MessagingToolPolicyOptions { Enabled = true, RequireEncryption = true };
        options.GrantPermissions(_agent, MessagingPermission.Discover, MessagingPermission.Send, MessagingPermission.Receive);
        var (broker, state) = CreateHarness(options);
        var convId = AddValidAgentConversation(state);

        // Resolve recipient
        var resolveReq = new ToolAccessRequest(_agent, new ToolId(MessagingToolIds.ResolveRecipient), _human, _tenant, _task);
        var resolveRes = await broker.InvokeAsync(resolveReq, JsonSerializer.SerializeToElement(new { principal_id = _human.Value.ToString() }));
        Assert.True(resolveRes.GetProperty("resolved").GetBoolean());

        // Send encrypted message
        var sendReq = new ToolAccessRequest(_agent, new ToolId(MessagingToolIds.Send), _human, _tenant, _task);
        var sendRes = await broker.InvokeAsync(sendReq, JsonSerializer.SerializeToElement(new { conversation_id = convId.Value.ToString(), text = "Status update", client_operation_id = "op-ok-1" }));
        Assert.Equal("committed", sendRes.GetProperty("status").GetString());
        Assert.Equal(1, sendRes.GetProperty("sequence").GetInt64());

        // Get status
        var statusReq = new ToolAccessRequest(_agent, new ToolId(MessagingToolIds.GetStatus), _human, _tenant, _task);
        var statusRes = await broker.InvokeAsync(statusReq, JsonSerializer.SerializeToElement(new { conversation_id = convId.Value.ToString() }));
        Assert.Equal(1, statusRes.GetProperty("active_human_count").GetInt32());
    }

    private ConversationId AddValidAgentConversation(HarnessState state)
    {
        var convId = new ConversationId(Ulid.NewUlid());
        state.Conversations[convId] = new ConversationRecord(
            _tenant, "openconnect", convId, ConversationSecurityProfile.AgentE2EE, ConversationState.Active, 1, 1, 1, DateTimeOffset.UtcNow,
            [
                new ConversationParticipant(convId, ParticipantKind.Human, _human, null, ParticipantRole.Owner, ParticipantStatus.Active, 1),
                new ConversationParticipant(convId, ParticipantKind.Agent, null, _agent, ParticipantRole.OperationalAgent, ParticipantStatus.Active, 1)
            ]);
        return convId;
    }

    private (ToolBroker Broker, HarnessState State) CreateHarness(MessagingToolPolicyOptions options)
    {
        var state = new HarnessState(_tenant, _human);
        var policy = new MessagingToolAccessPolicy(new AllowAllPolicy(), options);
        var auth = new ToolAuthorizationService(policy);
        state.AuthService = auth;
        var catalog = new ToolCatalog(auth);
        var suite = new MessagingToolSuite(
            options,
            state,
            state,
            state,
            state,
            cryptoProvider: state,
            callCoordinator: state,
            approvalStore: state);
        suite.RegisterAll(catalog, _agent);
        return (new ToolBroker(catalog, auth), state);
    }

    private sealed class AllowAllPolicy : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class HarnessState(TenantId tenant, PrincipalId human) :
        IMessagingRecipientResolver,
        IMessagingAuthorizer,
        IMessagingConversationStore,
        IMessagingCoordinator,
        IMessagingCryptoProvider,
        IMessagingCallCoordinator,
        IMessagingApprovalStore
    {
        public ToolAuthorizationService AuthService { get; set; } = null!;
        public Dictionary<ConversationId, ConversationRecord> Conversations { get; } = [];
        public TypedApprovalDescriptor? LastSavedApproval { get; private set; }
        public string ProtocolName => "test-e2ee-v1";

        public ValueTask<MessagingActor?> ResolveHumanAsync(TenantId tenantId, string applicationId, PrincipalId principalId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MessagingActor?>(tenantId == tenant && principalId == human ? new MessagingActor(tenantId, applicationId, ParticipantKind.Human, principalId) : null);

        public ValueTask<MessagingActor?> ResolveAgentAsync(TenantId tenantId, string applicationId, AgentId agentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<MessagingActor?>(null);

        public ValueTask<MessagingAuthorizationDecision> AuthorizeAsync(MessagingActor actor, MessagingPermission permission, ConversationId? conversationId = null, MessagingActor? target = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessagingAuthorizationDecision(true));

        public ValueTask<ConversationRecord?> GetConversationAsync(TenantId tenantId, ConversationId conversationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Conversations.TryGetValue(conversationId, out var c) && c.TenantId == tenantId ? c : null);

        public ValueTask<MessageCommitResult> CommitMessageAsync(MessagingActor sender, ConversationId conversationId, EncryptedMessageEnvelope envelope, string clientOperationId, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            var msg = new MessageRecord(sender.TenantId, conversationId, new MessageId(Ulid.NewUlid()), 1, sender.Kind, sender.PrincipalId, sender.AgentId, sender.DeviceId, envelope, clientOperationId, "sha256", DateTimeOffset.UtcNow, correlationId);
            return ValueTask.FromResult(new MessageCommitResult(msg, false));
        }

        public ValueTask<EncryptedMessageEnvelope> EncryptAsync(ConversationId conversationId, DeviceId senderDeviceId, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EncryptedMessageEnvelope(ProtocolName, 1, Convert.ToBase64String(plaintext.ToArray()), "nonce", "tag"));

        public ValueTask<TypedApprovalDescriptor> SaveRequestAsync(TypedApprovalDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            LastSavedApproval = descriptor;
            return ValueTask.FromResult(descriptor);
        }

        public ValueTask<MessagingCallRecord> CreateCallAsync(MessagingActor caller, ConversationId conversationId, CallMediaSecurityMode mediaSecurityMode, bool requireEncryption, TimeSpan? ringTimeout = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new MessagingCallRecord(caller.TenantId, caller.ApplicationId, new CallId(Ulid.NewUlid()), conversationId, caller.Kind, caller.PrincipalId, caller.AgentId, [human], CallState.Invited, mediaSecurityMode, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30)));

        public ValueTask<ConversationRecord> CreateConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ConversationRecord> UpdateConversationAsync(ConversationRecord conversation, long expectedMembershipVersion, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessageRecord?> GetMessageByClientOperationIdAsync(TenantId tenantId, ConversationId conversationId, string clientOperationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<MessageRecord?>(null);
        public ValueTask<MessageRecord> CommitMessageAsync(MessageRecord message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessageRecord?> GetMessageAsync(TenantId tenantId, ConversationId conversationId, MessageId messageId, CancellationToken cancellationToken = default) => ValueTask.FromResult<MessageRecord?>(null);
        public ValueTask<MessageReceiptRecord?> GetReceiptAsync(TenantId tenantId, MessageId messageId, DeviceId deviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult<MessageReceiptRecord?>(null);
        public ValueTask<MessageReceiptRecord> UpsertReceiptAsync(MessageReceiptRecord receipt, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ConversationRecord> CreateConversationAsync(MessagingActor creator, ConversationSecurityProfile securityProfile, IReadOnlyList<MessagingActor> initialParticipants, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ConversationRecord> AddParticipantAsync(MessagingActor actor, ConversationId conversationId, MessagingActor participantToAdd, ParticipantRole role, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<ParticipantRemovalResult> RemoveParticipantAsync(MessagingActor actor, ConversationId conversationId, ParticipantKind targetKind, PrincipalId? targetPrincipalId, AgentId? targetAgentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessageReceiptRecord> RecordReceiptAsync(MessagingActor actor, ConversationId conversationId, MessageId messageId, MessageReceiptKind kind, string? failureReason = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<bool> VerifyDeviceProofOfPossessionAsync(string publicKeyBase64, ReadOnlyMemory<byte> challenge, ReadOnlyMemory<byte> signature, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<string> ComputeKeyFingerprintAsync(string publicKeyBase64, CancellationToken cancellationToken = default) => ValueTask.FromResult("fp");
        public ValueTask<MessagingCryptoGroupState> CreateGroupAsync(ConversationId conversationId, IReadOnlyList<DeviceKeyPackage> initialMemberPackages, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessagingCryptoGroupState> AddDeviceAndRekeyAsync(ConversationId conversationId, DeviceKeyPackage newMemberPackage, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessagingCryptoGroupState> RemoveDeviceAndRekeyAsync(ConversationId conversationId, DeviceId removedDeviceId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<byte[]> DecryptAsync(ConversationId conversationId, DeviceId recipientDeviceId, EncryptedMessageEnvelope envelope, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessagingCallRecord> MarkRingingAsync(TenantId tenantId, CallId callId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessagingCallRecord> AcceptCallAsync(MessagingActor acceptor, CallId callId, SessionId voiceSessionId, string admissionTokenHash, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessagingCallRecord> DeclineCallAsync(MessagingActor actor, CallId callId, string? reason = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<MessagingCallRecord> EndCallAsync(MessagingActor actor, CallId callId, string? reason = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<int> TerminateCallsForConversationAsync(TenantId tenantId, ConversationId conversationId, string reason, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
    }
}
