using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafDurableSessionTests
{
    [Fact]
    public async Task Durable_turn_restores_native_history_into_a_new_runner()
    {
        var store = new MemoryStore();
        var client = new Client();
        var binding = Binding();
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        var (first, context) = Runner(client, binding);
        await first.TurnDurableAsync("first", context, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, new Gate()));
        var (resumed, context2) = Runner(client, binding);

        await resumed.TurnDurableAsync("continued", context2, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, new Gate()));

        Assert.Contains(client.LastRequest!, message => message.Role == ChatRole.User && message.Text == "first");
        Assert.Contains(client.LastRequest!, message => message.Role == ChatRole.Assistant && message.Text == "reply");
        Assert.Equal(new[] { (ChatRole.User, "continued"), (ChatRole.Assistant, "reply") },
            resumed.History.Select(message => (message.Role, message.Text)).ToArray());
    }

    [Fact]
    public async Task Mismatch_and_revocation_fail_before_persistence_load()
    {
        var binding = Binding();
        var store = new MemoryStore();
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        var (runner, context) = Runner(new Client(), binding);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.TurnDurableAsync("x", context with { TenantScope = "wrong" }, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, new Gate())));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.TurnDurableAsync("x", context, (_, _) => ValueTask.FromResult(true), Persistence(persistence, binding, new Gate())));
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task Contention_does_not_call_model_and_ownership_is_disposed()
    {
        var binding = Binding(); var client = new Client(); var (runner, context) = Runner(client, binding);
        var ownership = new Ownership();
        var gate = new Gate { Reject = true };
        var persistence = new NativeAgentSessionPersistence(new MemoryStore(), new PassthroughProtector());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnDurableAsync("x", context, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, gate)));
        Assert.Equal(0, client.Calls);
        gate.Reject = false;
        gate.Handle = ownership;
        await runner.TurnDurableAsync("ok", context, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, gate));
        Assert.True(ownership.Disposed);
        Assert.Equal(1, ownership.DisposeCalls);
    }

    [Fact]
    public async Task Cas_conflict_does_not_publish_response_or_advance_history()
    {
        var binding = Binding(); var client = new Client(); var (runner, context) = Runner(client, binding);
        var store = new MemoryStore { Conflict = true };
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnDurableAsync("x", context, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, new Gate())));
        Assert.Empty(runner.History);
        Assert.DoesNotContain(runner.GetTrace(), trace => trace.CompletedAt is null);
    }

    [Fact]
    public async Task Ownership_loss_cancels_model_and_prevents_save()
    {
        var binding = Binding(); var store = new MemoryStore();
        var lost = new CancellationTokenSource();
        var client = new Client { OnCall = _ => lost.Cancel() };
        var (runner, context) = Runner(client, binding);
        var gate = new Gate { Handle = new Ownership(lost.Token) };
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.TurnDurableAsync("x", context, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, gate)));
        Assert.Equal(0, store.Saves);
        Assert.True(((Ownership)gate.Handle!).Disposed);
    }

    [Fact]
    public async Task Dispose_failure_after_commit_returns_success_and_poisoned_runner_rejects_future_turns()
    {
        var binding = Binding(); var client = new Client(); var store = new MemoryStore();
        var (runner, context) = Runner(client, binding);
        var gate = new Gate { Handle = new Ownership { ThrowOnDispose = true } };
        var persistence = new NativeAgentSessionPersistence(store, new PassthroughProtector());

        var response = await runner.TurnDurableAsync("committed", context, (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, gate));

        Assert.Equal("reply", response.Text);
        Assert.False(runner.IsUsable);
        Assert.Equal(1, store.Saves);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnAsync("must not run"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnDurableAsync("must not run", context,
            (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, new Gate())));
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, store.Loads);
        Assert.Equal(1, ((Ownership)gate.Handle!).DisposeCalls);
    }

    [Fact]
    public async Task Dispose_failure_does_not_mask_turn_failure_and_cleanup_runs_once()
    {
        var binding = Binding(); var client = new Client { Fail = true }; var (runner, context) = Runner(client, binding);
        var ownership = new Ownership { ThrowOnDispose = true };
        var gate = new Gate { Handle = ownership };
        var persistence = new NativeAgentSessionPersistence(new MemoryStore(), new PassthroughProtector());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnDurableAsync("fails", context,
            (_, _) => ValueTask.FromResult(false), Persistence(persistence, binding, gate)));

        Assert.Equal("model failure", failure.Message);
        Assert.Equal(1, ownership.DisposeCalls);
    }

    [Fact]
    public async Task Poisoned_runner_rejects_delegation_before_budget_agent_trace_or_model_effects()
    {
        var binding = Binding();
        var client = new Client();
        var ledger = new RunBudgetLedger(new AgentBudget(10, 10, 1000, null, 2, 2, 10));
        var (runner, context) = Runner(client, binding, ledger);
        var persistence = new NativeAgentSessionPersistence(new MemoryStore(), new PassthroughProtector());
        await runner.TurnDurableAsync("commit", context, (_, _) => ValueTask.FromResult(false),
            Persistence(persistence, binding, new Gate { Handle = new Ownership { ThrowOnDispose = true } }));
        var budgetBefore = ledger.Remaining;
        var traceBefore = runner.GetTrace();
        var callsBefore = client.Calls;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DelegateAsync(Guid.NewGuid(), new AgentId("lead"), "blocked"));

        Assert.Equal(budgetBefore, ledger.Remaining);
        Assert.Equal(traceBefore, runner.GetTrace());
        Assert.Equal(callsBefore, client.Calls);
    }

    private static (MafConversationRunner Runner, AgentExecutionContext Context) Runner(Client client,
        NativeAgentSessionBinding binding, RunBudgetLedger? ledger = null)
    {
        var registry = new Registry();
        var factory = new MafRealAgentFactory(registry, client);
        var context = new AgentExecutionContext
        {
            TenantScope = binding.TenantId.Value.ToString(), PrincipalOrWorkloadScope = binding.PrincipalId.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser, SessionId = binding.SessionId, TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1, PolicyRevision = 1,
            AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(2), RemainingBudgets = ImmutableDictionary<string,long>.Empty,
            CancellationToken = CancellationToken.None
        };
        return (new MafConversationRunner(factory, new AgentId("lead"), runLedger: ledger), context);
    }

    private static NativeAgentSessionBinding Binding() => new(new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()), "lead", "rev1", "maf-test", "schema1", "provider1");
    private static NativeSessionTurnPersistenceContext Persistence(NativeAgentSessionPersistence persistence, NativeAgentSessionBinding binding, INativeSessionTurnGate gate) => new(persistence, binding, gate);

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
        public bool Conflict { get; init; }
        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Loads++; return Task.FromResult(_record); }
        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Saves++;
            if (Conflict || (_record?.Version ?? 0) != expectedVersion) return Task.FromResult(NativeAgentSessionSaveOutcome.Conflict);
            _record = record; return Task.FromResult(NativeAgentSessionSaveOutcome.Saved);
        }
    }

    private sealed class PassthroughProtector : INativeAgentSessionProtector
    {
        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => Task.FromResult(plaintext.ToArray());
        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) => Task.FromResult(ciphertext.ToArray());
    }

    private sealed class Gate : INativeSessionTurnGate
    {
        public Ownership? Handle { get; set; }
        public bool Reject { get; set; }
        public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding, AgentExecutionContext executionContext, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<INativeSessionTurnOwnership?>(Reject ? null : Handle ?? new Ownership()); }
    }

    private sealed class Ownership(CancellationToken lost = default) : INativeSessionTurnOwnership
    {
        private readonly CancellationToken _lost = lost;
        public CancellationToken OwnershipLost => _lost;
        public bool Disposed { get; private set; }
        public bool ThrowOnDispose { get; init; }
        public int DisposeCalls { get; private set; }
        public Task ValidateOwnershipAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _lost.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCalls++; Disposed = true; return ThrowOnDispose ? ValueTask.FromException(new InvalidOperationException("cleanup diagnostic")) : ValueTask.CompletedTask; }
    }

    private sealed class Client : IChatClient
    {
        public int Calls { get; private set; }
        public IReadOnlyList<ChatMessage>? LastRequest { get; private set; }
        public Action<CancellationToken>? OnCall { get; init; }
        public bool Fail { get; init; }
        public ChatClientMetadata Metadata { get; } = new("durable-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; OnCall?.Invoke(cancellationToken); if (Fail) throw new InvalidOperationException("model failure"); LastRequest = messages.ToArray(); return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply"))); }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var response = await GetResponseAsync(messages, options, cancellationToken); yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text); }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
