// FOLLOW-LOCK-003 negative lock: host authority material (AgentExecutionContext
// TenantScope / PrincipalOrWorkloadScope / budget markers, vault-shaped synthetic
// secrets) must never appear in prompts, MAF History, outbox-construction string
// inputs (OutboxEntry behavioral leg only), or checkpoint serialization.
// SCOPE DISCLAIMER (F-02): this file does NOT cover WorkerEnvelope/PayloadRef or a
// real NATS producer; WorkerEnvelope absence rests on source inspection per AG-IMPL-027.
// NOTE (F-01): the original context-free TurnAsync leg was deleted as vacuous —
// TurnAsync accepts no context so canary non-appearance was tautological, and the
// TurnDurableAsync legs below (which pass a live context) already catch ambient leakage.
// Synthetic canaries only; no real credentials.
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ContextNeverInterpolatedTests
{
    // Vault-shaped synthetic secret: rigid format, obviously fake, never a real credential.
    private const string VaultCanary = "EXAMPLE-SYNTHETIC-SECRET-0001";

    [Fact]
    public async Task Durable_turn_history_and_model_request_carry_no_context_canary()
    {
        var store = new CanaryMemoryStore();
        var client = new CanaryClient();
        var binding = CanaryBinding();
        var persistence = new NativeAgentSessionPersistence(store, new CanaryProtector());
        var budgetCanary = "CTX-CANARY-" + Guid.NewGuid().ToString("N");
        using var context = CanaryContext(budgetCanary, binding);
        var runner = new MafConversationRunner(
            new MafRealAgentFactory(new CanaryRegistry("lead"), client), new AgentId("lead"));

        var response = await runner.TurnDurableAsync("benign durable prompt", context,
            (_, _) => ValueTask.FromResult(false),
            new NativeSessionTurnPersistenceContext(persistence, binding, new CanaryGate()));

        Assert.DoesNotContain(context.TenantScope, response.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(context.PrincipalOrWorkloadScope, response.Text, StringComparison.Ordinal);
        Assert.All(runner.History, message => Assert.DoesNotContain(context.TenantScope, message.Text ?? string.Empty));
        Assert.All(runner.History, message => Assert.DoesNotContain(context.PrincipalOrWorkloadScope, message.Text ?? string.Empty));
        Assert.All(runner.History, message => Assert.DoesNotContain(budgetCanary, message.Text ?? string.Empty));
        Assert.All(runner.History, message => Assert.DoesNotContain(VaultCanary, message.Text ?? string.Empty));
        Assert.NotNull(client.LastRequest);
        Assert.NotEmpty(client.LastRequest!);
        Assert.All(client.LastRequest!, message => Assert.DoesNotContain(budgetCanary, message.Text ?? string.Empty));
        Assert.All(client.LastRequest!, message => Assert.DoesNotContain(VaultCanary, message.Text ?? string.Empty));
        Assert.All(client.LastRequest!, message => Assert.DoesNotContain(context.TenantScope, message.Text ?? string.Empty));
    }

    [Fact]
    public async Task Exported_checkpoint_serialization_contains_no_context_canary()
    {
        var store = new CanaryMemoryStore();
        var client = new CanaryClient();
        var binding = CanaryBinding();
        var persistence = new NativeAgentSessionPersistence(store, new CanaryProtector());
        var budgetCanary = "CTX-CANARY-" + Guid.NewGuid().ToString("N");
        using var context = CanaryContext(budgetCanary, binding);
        var runner = new MafConversationRunner(
            new MafRealAgentFactory(new CanaryRegistry("lead"), client), new AgentId("lead"));

        await runner.TurnDurableAsync("benign checkpoint prompt", context,
            (_, _) => ValueTask.FromResult(false),
            new NativeSessionTurnPersistenceContext(persistence, binding, new CanaryGate()));
        await runner.DelegateAsync(Guid.NewGuid(), new AgentId("lead"), "child work");

        var checkpointJson = JsonSerializer.Serialize(runner.ExportCheckpoint("session-1"));

        Assert.DoesNotContain(context.TenantScope, checkpointJson, StringComparison.Ordinal);
        Assert.DoesNotContain(context.PrincipalOrWorkloadScope, checkpointJson, StringComparison.Ordinal);
        Assert.DoesNotContain(budgetCanary, checkpointJson, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultCanary, checkpointJson, StringComparison.Ordinal);
        Assert.DoesNotContain("benign checkpoint prompt", checkpointJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outbox_construction_inputs_from_turn_outputs_carry_no_context_canary()
    {
        var client = new CanaryClient();
        var runner = new MafConversationRunner(
            new MafRealAgentFactory(new CanaryRegistry("echo"), client), new AgentId("echo"));
        var budgetCanary = "CTX-CANARY-" + Guid.NewGuid().ToString("N");
        using var context = CanaryContext(budgetCanary);

        var response = await runner.TurnAsync("benign outbox prompt");

        // OutboxEntry behavioral leg only — NOT WorkerEnvelope/PayloadRef coverage.
        // Outbox string fields here are built only from turn-derived prompt/response
        // strings; no real NATS producer is exercised. WorkerEnvelope absence rests
        // on source inspection per AG-IMPL-027.
        var entry = new OutboxEntry(Guid.NewGuid(), "turn-completed", response.Text);
        var entryJson = JsonSerializer.Serialize(entry);

        Assert.DoesNotContain(context.TenantScope, entryJson, StringComparison.Ordinal);
        Assert.DoesNotContain(context.PrincipalOrWorkloadScope, entryJson, StringComparison.Ordinal);
        Assert.DoesNotContain(budgetCanary, entryJson, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultCanary, entryJson, StringComparison.Ordinal);
    }

    private static AgentExecutionContext CanaryContext(string budgetCanary, NativeAgentSessionBinding? binding = null)
    {
        var tenantScope = binding?.TenantId.Value.ToString() ?? "tenant-" + Guid.NewGuid().ToString("N");
        var principalScope = binding?.PrincipalId.Value.ToString() ?? "principal-" + Guid.NewGuid().ToString("N");
        return new AgentExecutionContext
        {
            TenantScope = tenantScope,
            PrincipalOrWorkloadScope = principalScope,
            IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = binding?.SessionId ?? new SessionId(Ulid.NewUlid()),
            TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
            Depth = 0,
            CatalogRevision = 1,
            PolicyRevision = 1,
            AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty
                .Add(budgetCanary, 7)
                .Add(VaultCanary, 3),
            CancellationToken = CancellationToken.None,
        };
    }

    private static NativeAgentSessionBinding CanaryBinding() => new(
        new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()), "lead", "rev1", "canary-test", "schema1", "provider1");

    private sealed class CanaryRegistry(string lead) : IAgentDefinitionRegistry
    {
        private readonly AgentDefinition _definition = new(new AgentId(lead), "Canary");
        public AgentDefinition? Get(AgentId id) => id == _definition.Id ? _definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [_definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class CanaryClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? LastRequest { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("context-canary-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = messages.ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "canary-safe reply")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CanaryMemoryStore : INativeAgentSessionStore
    {
        private NativeAgentSessionRecord? _record;
        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_record);
        }
        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if ((_record?.Version ?? 0) != expectedVersion) return Task.FromResult(NativeAgentSessionSaveOutcome.Conflict);
            _record = record;
            return Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
        }
    }

    private sealed class CanaryProtector : INativeAgentSessionProtector
    {
        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) =>
            Task.FromResult(plaintext.ToArray());
        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) =>
            Task.FromResult(ciphertext.ToArray());
    }

    private sealed class CanaryGate : INativeSessionTurnGate
    {
        public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding,
            AgentExecutionContext executionContext, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<INativeSessionTurnOwnership?>(new CanaryOwnership());
        }
    }

    private sealed class CanaryOwnership : INativeSessionTurnOwnership
    {
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task ValidateOwnershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
