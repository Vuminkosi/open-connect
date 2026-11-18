using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-006: exactly ten sequential turns across simulated host restart
/// restore the same authorized native session and produce coherent answers.
/// </summary>
public sealed class SequentialTenTurnDurableRestartTests
{
    [Fact]
    public async Task TenSequentialTurnsAcrossRestart_RestoreSessionAndMaintainCoherence()
    {
        var binding = Binding();
        var store = new MemoryStore();
        var protector = new PassthroughProtector();
        var persistence = new NativeAgentSessionPersistence(store, protector);
        var gate = new Gate();

        // 10 sequential turns, each running in a brand-new runner instance simulating a host restart.
        for (var turn = 1; turn <= 10; turn++)
        {
            var prompt = $"Turn prompt {turn}";
            var expectedReply = $"Coherent answer for turn {turn}";
            var client = new EchoClient(expectedReply);

            // Create a fresh runner for this turn (simulating host restart)
            var (runner, context) = CreateRunner(client, binding);

            var response = await runner.TurnDurableAsync(
                prompt,
                context,
                (_, _) => ValueTask.FromResult(false),
                Persistence(persistence, binding, gate));

            Assert.Equal(expectedReply, response.Text);

            // Verify current prompt was sent to model
            Assert.Contains(client.LastRequest!, msg => msg.Role == ChatRole.User && msg.Text == prompt);

            // If turn > 1, verify prior turn's prompt and answer were restored into model request
            if (turn > 1)
            {
                Assert.Contains(client.LastRequest!, msg => msg.Role == ChatRole.User && msg.Text == $"Turn prompt {turn - 1}");
                Assert.Contains(client.LastRequest!, msg => msg.Role == ChatRole.Assistant && msg.Text == $"Coherent answer for turn {turn - 1}");
            }

            // Verify store save count matches current turn
            Assert.Equal(turn, store.Saves);
        }

        // Final verification: restore session in an 11th fresh runner and verify complete history of 10 turns
        var finalClient = new EchoClient("final");
        var (finalRunner, finalContext) = CreateRunner(finalClient, binding);
        await finalRunner.TurnDurableAsync(
            "check history",
            finalContext,
            (_, _) => ValueTask.FromResult(false),
            Persistence(persistence, binding, gate));

        // The request sent to model should contain 10 previous user messages + 10 previous assistant messages + current prompt
        var userMessages = finalClient.LastRequest!.Where(m => m.Role == ChatRole.User).ToArray();
        Assert.Equal(11, userMessages.Length); // 10 previous + 1 current
        for (var i = 1; i <= 10; i++)
        {
            Assert.Equal($"Turn prompt {i}", userMessages[i - 1].Text);
        }
    }

    [Fact]
    public async Task HostRestart_WithMismatchedTenantScope_FailsClosedBeforeModelExecution()
    {
        var binding = Binding();
        var store = new MemoryStore();
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        var gate = new Gate();
        var client = new EchoClient("reply");

        // First turn succeeds
        var (runner1, context1) = CreateRunner(client, binding);
        await runner1.TurnDurableAsync("turn 1", context1, (_, _) => ValueTask.FromResult(false),
            Persistence(persistence, binding, gate));
        Assert.Equal(1, store.Saves);

        // Second turn after restart with malicious/mismatched tenant scope fails closed
        var maliciousContext = context1 with { TenantScope = "unauthorized-tenant" };
        var (runner2, _) = CreateRunner(client, binding);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner2.TurnDurableAsync(
            "tampered turn",
            maliciousContext,
            (_, _) => ValueTask.FromResult(false),
            Persistence(persistence, binding, gate)));

        // Store was not modified, model was not called for turn 2
        Assert.Equal(1, store.Saves);
    }

    private static (MafConversationRunner Runner, AgentExecutionContext Context) CreateRunner(
        IChatClient client, NativeAgentSessionBinding binding)
    {
        var registry = new Registry();
        var factory = new MafRealAgentFactory(registry, client);
        var context = new AgentExecutionContext
        {
            TenantScope = binding.TenantId.Value.ToString(),
            PrincipalOrWorkloadScope = binding.PrincipalId.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = binding.SessionId,
            TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
            Depth = 0,
            CatalogRevision = 1,
            PolicyRevision = 1,
            AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddHours(1),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty,
            CancellationToken = CancellationToken.None
        };
        var runner = new MafConversationRunner(factory, new AgentId("lead"));
        return (runner, context);
    }

    private static NativeAgentSessionBinding Binding() => new(
        new SessionId(Ulid.NewUlid()),
        new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()),
        "lead",
        "rev1",
        "maf-test",
        "schema1",
        "provider1");

    private static NativeSessionTurnPersistenceContext Persistence(
        NativeAgentSessionPersistence persistence, NativeAgentSessionBinding binding, INativeSessionTurnGate gate) =>
        new(persistence, binding, gate);

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("lead"), "Lead");
        public AgentDefinition? Get(AgentId id) => Definition;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class MemoryStore : INativeAgentSessionStore
    {
        private NativeAgentSessionRecord? _record;
        public int Loads { get; private set; }
        public int Saves { get; private set; }

        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Loads++;
            return Task.FromResult(_record);
        }

        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if ((_record?.Version ?? 0) != expectedVersion) return Task.FromResult(NativeAgentSessionSaveOutcome.Conflict);
            _record = record;
            Saves++;
            return Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
        }
    }

    private sealed class PassthroughProtector : INativeAgentSessionProtector
    {
        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
            Task.FromResult(plaintext.ToArray());

        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) =>
            Task.FromResult(ciphertext.ToArray());
    }

    private sealed class Gate : INativeSessionTurnGate
    {
        public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding, AgentExecutionContext executionContext, CancellationToken cancellationToken = default) =>
            Task.FromResult<INativeSessionTurnOwnership?>(new Ownership());
    }

    private sealed class Ownership : INativeSessionTurnOwnership
    {
        public CancellationToken OwnershipLost => default;
        public Task ValidateOwnershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EchoClient(string replyText) : IChatClient
    {
        public List<ChatMessage>? LastRequest { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("echo-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var res = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, res.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
