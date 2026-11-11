// <copyright file="AdversarialAuthorizationTests.cs">
// Adversarial security tests for OpenConnect.Agents (OC2-D-AUTH-016).
// Covers:
// 1. Cross-tenant session IDs beyond approval scope
// 2. Revoked rights on reconnect beyond expiry
// 3. Compromised worker payload / privilege injection
// 4. Malicious child contexts attempting privilege widening or exceeding depth ceilings
// </copyright>

using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class AdversarialAuthorizationTests
{
    private static readonly AgentId LeadId = new("lead-agent");
    private static readonly AgentId ChildId = new("child-agent");
    private static readonly ToolId BenignTool = new("read_docs");
    private static readonly ToolId SensitiveTool = new("delete_records");

    [Fact]
    public async Task CrossTenant_SessionId_Denied_FailClosed_WithZeroExecution()
    {
        var client = new TestChatClient();
        var leadDef = new AgentDefinition(LeadId, "Lead");
        var factory = new MafRealAgentFactory(new TestRegistry(leadDef), client);
        var runner = new MafConversationRunner(factory, LeadId);

        var tenantA = new TenantId(Ulid.NewUlid());
        var tenantB = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sharedSessionId = new SessionId(Ulid.NewUlid());

        // Context belongs to Tenant A
        var contextA = new AgentExecutionContext
        {
            TenantScope = tenantA.Value.ToString(),
            PrincipalOrWorkloadScope = principal.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = sharedSessionId,
            TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
            Depth = 0,
            CatalogRevision = 1,
            PolicyRevision = 1,
            AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty,
            CancellationToken = CancellationToken.None
        };

        // Adversarial attempt: binding belongs to Tenant B
        var bindingB = new NativeAgentSessionBinding(
            sharedSessionId,
            tenantB,
            principal,
            LeadId.Value,
            "rev1",
            "maf-1.27.0",
            "schema-1",
            "test-provider");

        var store = new MemoryStore();
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        var persistenceContext = new NativeSessionTurnPersistenceContext(persistence, bindingB, new PassthroughGate());

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runner.TurnDurableAsync("adversarial prompt", contextA, (_, _) => ValueTask.FromResult(false), persistenceContext));

        Assert.Contains("identity does not match", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task Reconnect_WithExpiredDeadline_Denied_FailClosed()
    {
        var client = new TestChatClient();
        var leadDef = new AgentDefinition(LeadId, "Lead");
        var factory = new MafRealAgentFactory(new TestRegistry(leadDef), client);
        var runner = new MafConversationRunner(factory, LeadId);

        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessionId = new SessionId(Ulid.NewUlid());

        var binding = new NativeAgentSessionBinding(
            sessionId,
            tenant,
            principal,
            LeadId.Value,
            "rev1",
            "maf-1.27.0",
            "schema-1",
            "test-provider");

        // Expired deadline on reconnect
        var expiredContext = new AgentExecutionContext
        {
            TenantScope = tenant.Value.ToString(),
            PrincipalOrWorkloadScope = principal.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = sessionId,
            TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
            Depth = 0,
            CatalogRevision = 1,
            PolicyRevision = 1,
            AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(-30),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty,
            CancellationToken = CancellationToken.None
        };

        var store = new MemoryStore();
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        var persistenceContext = new NativeSessionTurnPersistenceContext(persistence, binding, new PassthroughGate());

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runner.TurnDurableAsync("reconnect prompt", expiredContext, (_, _) => ValueTask.FromResult(false), persistenceContext));

        Assert.Contains("expired or authorization has been revoked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public void MaliciousChild_AttemptingToolWidening_DeniedByDelegationGate()
    {
        var catalog = new AgentCatalog();
        var parentDef = new AgentDefinition(LeadId, "Parent")
        {
            ToolPolicy = new AgentToolPolicy { AllowedToolIds = [BenignTool] },
            Orchestration = new OrchestrationPolicy(MaxDepth: 2, AllowedChildAgents: [ChildId])
        };

        var childDef = new AgentDefinition(ChildId, "Child")
        {
            ToolPolicy = new AgentToolPolicy { AllowedToolIds = [BenignTool, SensitiveTool] }
        };

        catalog.Register(childDef);
        catalog.Register(parentDef);

        var gate = new DelegationGate(catalog);

        var parentContext = new DelegationParentContext(
            LeadId,
            Guid.NewGuid(),
            Depth: 0,
            Ancestors: [],
            EffectiveToolIds: new HashSet<ToolId> { BenignTool },
            EffectiveScopes: new HashSet<string> { "read" },
            RemainingBudget: new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
            ExecutionMode: AgentExecutionMode.Interactive);

        // Adversarial request: child asks for SensitiveTool (privilege expansion not in parent's effective grant)
        var maliciousRequest = new DelegationRequest(
            ToolIds: new HashSet<ToolId> { SensitiveTool },
            Scopes: new HashSet<string> { "read" });

        var ex = Assert.Throws<AgentCatalogException>(() =>
            gate.AuthorizeDelegation(parentContext, ChildId, maliciousRequest));

        Assert.Contains("exceed the parent's effective grant", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaliciousChild_AttemptingDepth3_DeniedByDelegationGate()
    {
        var catalog = new AgentCatalog();
        var parentDef = new AgentDefinition(LeadId, "Parent")
        {
            Orchestration = new OrchestrationPolicy(MaxDepth: 2, AllowedChildAgents: [ChildId])
        };
        var childDef = new AgentDefinition(ChildId, "Child");

        catalog.Register(childDef);
        catalog.Register(parentDef);

        var gate = new DelegationGate(catalog);

        var depth2Context = new DelegationParentContext(
            LeadId,
            Guid.NewGuid(),
            Depth: 2, // Already at maximum depth 2
            Ancestors: [],
            EffectiveToolIds: new HashSet<ToolId> { BenignTool },
            EffectiveScopes: new HashSet<string> { "read" },
            RemainingBudget: new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
            ExecutionMode: AgentExecutionMode.Interactive);

        var request = new DelegationRequest(new HashSet<ToolId>(), new HashSet<string>());

        var ex = Assert.Throws<DelegationDepthExceededException>(() =>
            gate.AuthorizeDelegation(depth2Context, ChildId, request));

        Assert.Contains("exceeds the maximum depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestRegistry(AgentDefinition def) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => id == def.Id ? def : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [def];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class TestChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var res = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, res.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MemoryStore : INativeAgentSessionStore
    {
        public int Loads { get; private set; }
        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default)
        {
            Loads++;
            return Task.FromResult<NativeAgentSessionRecord?>(null);
        }
        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
            => Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
    }

    private sealed class PassthroughProtector : INativeAgentSessionProtector
    {
        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => Task.FromResult(plaintext.ToArray());
        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) => Task.FromResult(ciphertext.ToArray());
    }

    private sealed class PassthroughGate : INativeSessionTurnGate
    {
        public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding, AgentExecutionContext executionContext, CancellationToken cancellationToken = default)
            => Task.FromResult<INativeSessionTurnOwnership?>(new PassthroughOwnership());
    }

    private sealed class PassthroughOwnership : INativeSessionTurnOwnership
    {
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task ValidateOwnershipAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
