// <copyright file="RunnerResumeCatalogFreezeTests.cs">
// Tests for runner-level catalog revision freeze and compatibility enforcement on resume (OC2-D-CONFIG-016).
// Wires pinned catalog revision into TurnDurableAsync and ImportCheckpoint resume paths.
// </copyright>

using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class RunnerResumeCatalogFreezeTests
{
    private static readonly AgentId LeadId = new("lead");
    private static readonly AgentDefinition LeadDef = new(LeadId, "Lead");

    [Fact]
    public async Task TurnDurableAsync_WithMatchingPinnedCatalogRevision_Succeeds()
    {
        var client = new TestChatClient();
        var factory = new MafRealAgentFactory(new TestRegistry(LeadDef), client);
        const string pinnedRevision = "rev-frozen-abc123";

        var runner = new MafConversationRunner(factory, LeadId, pinnedCatalogRevision: pinnedRevision);

        var (context, binding, persistenceContext) = CreateContextAndBinding(definitionRevision: pinnedRevision);

        var response = await runner.TurnDurableAsync(
            "hello durable",
            context,
            (_, _) => ValueTask.FromResult(false),
            persistenceContext);

        Assert.NotNull(response);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task TurnDurableAsync_WithMismatchedCatalogRevision_FailsClosed_BeforeExecution()
    {
        var client = new TestChatClient();
        var factory = new MafRealAgentFactory(new TestRegistry(LeadDef), client);
        const string pinnedRevision = "rev-frozen-abc123";

        var runner = new MafConversationRunner(factory, LeadId, pinnedCatalogRevision: pinnedRevision);

        // Binding has an incompatible definition revision
        var (context, binding, persistenceContext) = CreateContextAndBinding(definitionRevision: "rev-mutated-xyz999");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.TurnDurableAsync(
                "hello durable",
                context,
                (_, _) => ValueTask.FromResult(false),
                persistenceContext));

        Assert.Contains("does not match the runner's pinned catalog revision", ex.Message);
        Assert.Equal(0, client.Calls); // Fails closed before any MAF invocation!
    }

    [Fact]
    public void ImportCheckpoint_WithMatchingCatalogRevision_Succeeds()
    {
        var client = new TestChatClient();
        var factory = new MafRealAgentFactory(new TestRegistry(LeadDef), client);
        const string pinnedRevision = "rev-pinned-42";

        var runner = new MafConversationRunner(factory, LeadId, pinnedCatalogRevision: pinnedRevision);

        var checkpoint = ConversationCheckpointCodec.Export(
            LeadId.Value,
            "session-1",
            turnSequence: 5,
            childSessions: [],
            catalogRevision: pinnedRevision);

        runner.ImportCheckpoint(checkpoint, "session-1");
        // Succeeds with no exception
    }

    [Fact]
    public void ImportCheckpoint_WithIncompatibleCatalogRevision_FailsClosed()
    {
        var client = new TestChatClient();
        var factory = new MafRealAgentFactory(new TestRegistry(LeadDef), client);
        const string pinnedRevision = "rev-pinned-42";

        var runner = new MafConversationRunner(factory, LeadId, pinnedCatalogRevision: pinnedRevision);

        var checkpoint = ConversationCheckpointCodec.Export(
            LeadId.Value,
            "session-1",
            turnSequence: 5,
            childSessions: [],
            catalogRevision: "rev-different-99");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            runner.ImportCheckpoint(checkpoint, "session-1"));

        Assert.Contains("catalog revision", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static (AgentExecutionContext Context, NativeAgentSessionBinding Binding, NativeSessionTurnPersistenceContext PersistenceContext)
        CreateContextAndBinding(string definitionRevision)
    {
        var sessionId = new SessionId(Ulid.NewUlid());
        var tenantId = new TenantId(Ulid.NewUlid());
        var principalId = new PrincipalId(Ulid.NewUlid());

        var binding = new NativeAgentSessionBinding(
            sessionId,
            tenantId,
            principalId,
            LeadId.Value,
            definitionRevision,
            "maf-1.27.0",
            "schema-1",
            "test-provider");

        var context = new AgentExecutionContext
        {
            TenantScope = tenantId.Value.ToString(),
            PrincipalOrWorkloadScope = principalId.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = sessionId,
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

        var store = new MemoryStore();
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        var persistenceContext = new NativeSessionTurnPersistenceContext(persistence, binding, new PassthroughGate());

        return (context, binding, persistenceContext);
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
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "durable reply")));
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
        private NativeAgentSessionRecord? _record;
        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default) => Task.FromResult(_record);
        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
        {
            _record = record;
            return Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
        }
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
