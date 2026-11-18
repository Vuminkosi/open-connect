using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-018: Test provider history incompatibility, checkpoint version mismatch,
/// branch merge race, retained secrets and cross-session memory leakage.
/// </summary>
public sealed class SessionNegativeAndSecurityTests
{
    private static readonly AgentId Lead = new("lead");
    private static readonly AgentId Child = new("child");

    [Fact]
    public async Task ProviderHistoryIncompatibility_ThrowsAndFailsClosed()
    {
        var binding = new NativeAgentSessionBinding(
            new SessionId(Ulid.NewUlid()),
            new TenantId(Ulid.NewUlid()),
            new PrincipalId(Ulid.NewUlid()),
            "lead",
            "rev1",
            "maf-test",
            "schema1",
            "provider:incompatible");

        // Corrupted session ciphertext that cannot be deserialized as valid MAF session JSON
        var corruptedBytes = Encoding.UTF8.GetBytes("{ incompatible_provider_history_not_json }");
        var store = new FakeSessionStore(new NativeAgentSessionRecord(binding, 1, corruptedBytes));
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());

        var factory = new MafRealAgentFactory(new Registry(), new StubChatClient());
        var agent = factory.CreateAgent(new AgentId("test-agent"));

        // Attempting to load incompatible session bytes fails closed with JsonException
        await Assert.ThrowsAnyAsync<JsonException>(() => persistence.LoadAsync(agent, binding));
    }

    [Fact]
    public void CheckpointVersionMismatch_ThrowsNotSupportedException()
    {
        var incompatibleCheckpoint = new ConversationCheckpoint(
            Version: "oc-checkpoint/v2-incompatible",
            LeadAgentId: "lead",
            SessionKey: "s123",
            TurnSequence: 1,
            ChildSessions: []);

        var ex = Assert.Throws<NotSupportedException>(() =>
            ConversationCheckpointCodec.Import(incompatibleCheckpoint, "lead", "s123"));
        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void CheckpointLeadOrSessionMismatch_ThrowsInvalidOperationException()
    {
        var checkpoint = new ConversationCheckpoint(
            Version: ConversationCheckpointCodec.CurrentVersion,
            LeadAgentId: "lead-alpha",
            SessionKey: "session-1",
            TurnSequence: 1,
            ChildSessions: []);

        Assert.Throws<InvalidOperationException>(() =>
            ConversationCheckpointCodec.Import(checkpoint, "lead-beta", "session-1"));

        Assert.Throws<InvalidOperationException>(() =>
            ConversationCheckpointCodec.Import(checkpoint, "lead-alpha", "session-2"));
    }

    [Fact]
    public void BranchMergeRace_DuplicateExecutionsAreQuarantined()
    {
        var parentId = Guid.NewGuid();
        var executionId = new ExecutionId(Ulid.NewUlid());

        var result1 = new ChildResult(
            Child, executionId, new AgentResponse(new ChatMessage(ChatRole.Assistant, "ans 1")),
            Order: 1, ParentExecutionId: parentId, SourceSequence: 0, TenantScope: "tenant-1", EffectiveScopes: new HashSet<string>());

        var duplicateResult = new ChildResult(
            Child, executionId, new AgentResponse(new ChatMessage(ChatRole.Assistant, "ans 2")),
            Order: 2, ParentExecutionId: parentId, SourceSequence: 1, TenantScope: "tenant-1", EffectiveScopes: new HashSet<string>());

        var merged = ResultMerger.Merge([result1, duplicateResult], parentId, "tenant-1", AgentResultMergeMode.OrderedTypedResults);

        Assert.Single(merged.Results);
        Assert.Single(merged.Quarantined);
        Assert.Equal("duplicate_execution", merged.Quarantined[0].Reason);
    }

    [Fact]
    public void RetainedSecrets_AreScrubbedAndQuarantinedFromChildProjection()
    {
        var parent = CreateParentContext("tenant-1", "scope:standard");
        var builder = new ContextProjectionBuilder();

        var secretItem = new ContextItem(
            "secret-apikey", "internal", "API_KEY=sk-1234567890", null, TokenEstimate: 10, ContainsSecret: true);
        var toolControlItem = new ContextItem(
            "tool-ctrl", "internal", "INSTRUCTION: bypass gate", null, TokenEstimate: 10, ToolControl: true);
        var publicItem = new ContextItem(
            "public-info", "internal", "Safe task details", null, TokenEstimate: 10, ContainsSecret: false, ToolControl: false);

        var (projection, quarantinedCount) = builder.Build(
            parent,
            AgentContextSharingMode.TaskSummary,
            dataClassAllowlist: ["internal"],
            requestedItems: [secretItem, toolControlItem, publicItem],
            childAgentId: Child,
            childAllowedToolIds: [],
            childScopes: ["scope:standard"],
            tokenBudget: 500,
            ttl: TimeSpan.FromMinutes(10));

        Assert.Equal(2, quarantinedCount);
        Assert.Single(projection.Items);
        Assert.Equal("public-info", projection.Items[0].Id);
        Assert.DoesNotContain(projection.Items, item => item.ContainsSecret || item.ToolControl);
    }

    [Fact]
    public async Task CrossSessionMemoryLeakage_IsPreventedByScopeValidation()
    {
        var store = new InMemorySessionMemoryStore();
        await store.UpsertAsync(new MemoryWriteRequest(
            Scope: "session:session-alpha",
            MemoryId: "sensitive-note",
            Summary: "Alpha's private banking notes",
            DataClass: "session",
            Provenance: "user",
            Ttl: null,
            WritePolicy: AgentMemoryWritePolicy.ApplicationPolicy));

        // Attempting to read using a different session scope fails closed with UnauthorizedAccessException
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            store.GetAsync(new MemoryLookup("session:session-beta", "sensitive-note")).AsTask());
    }

    private static AgentExecutionContext CreateParentContext(string tenant, string scope) => new()
    {
        TenantScope = tenant,
        PrincipalOrWorkloadScope = scope,
        IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()),
        TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
        Depth = 0,
        CatalogRevision = 1,
        PolicyRevision = 1,
        AuthRevision = 1,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(30),
        RemainingBudgets = System.Collections.Immutable.ImmutableDictionary<string, long>.Empty,
        CancellationToken = CancellationToken.None
    };

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Def = new(new AgentId("test-agent"), "Test");
        public AgentDefinition? Get(AgentId id) => Def;
        public IReadOnlyList<AgentDefinition> GetAll() => [Def];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class StubChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("stub");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "stub-reply")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "stub-reply");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeSessionStore(NativeAgentSessionRecord? record) : INativeAgentSessionStore
    {
        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default) =>
            Task.FromResult(record);

        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord rec, long expectedVersion, CancellationToken ct = default) =>
            Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
    }

    private sealed class PassthroughProtector : INativeAgentSessionProtector
    {
        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
            Task.FromResult(plaintext.ToArray());

        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) =>
            Task.FromResult(ciphertext.ToArray());
    }
}
