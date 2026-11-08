using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class RunBudgetLedgerTests
{
    private static AgentBudget Budget(int subagents = 8, int tools = 20, int models = 12, long tokens = 100) =>
        new(tools, models, tokens, TimeSpan.FromMinutes(1), 3, 4, subagents);

    [Fact]
    public void Subagents_AreReservedOnceAndDeniedAfterRunLimit()
    {
        var ledger = new RunBudgetLedger(Budget());

        for (var i = 0; i < 8; i++) Assert.True(ledger.TryReserveSubagent(1, 0));
        Assert.False(ledger.TryReserveSubagent(1, 0));
        Assert.Equal(0, ledger.Remaining.MaxTotalAgents);
    }

    [Fact]
    public async Task ConcurrentSubagentReservations_AreAtomic()
    {
        var ledger = new RunBudgetLedger(Budget());
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => ledger.TryReserveSubagent(1, 0))));

        Assert.Equal(8, results.Count(x => x));
        Assert.Equal(0, ledger.Remaining.MaxTotalAgents);
    }

    [Fact]
    public async Task CombinedReservations_NeverExceedConcurrencyAndDebitOnlySuccessfulChildren()
    {
        var ledger = new RunBudgetLedger(Budget(subagents: 8));
        using var barrier = new Barrier(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return ledger.TryReserveSubagentWithSlot();
        })));

        Assert.Equal(3, results.Count(x => x));
        Assert.Equal(5, ledger.Remaining.MaxTotalAgents);
        Assert.False(ledger.TryReserveSubagentWithSlot());
        Assert.Equal(5, ledger.Remaining.MaxTotalAgents);
        for (var i = 0; i < 3; i++) Assert.True(ledger.ReleaseActiveChild());
        Assert.False(ledger.ReleaseActiveChild());
    }

    [Fact]
    public void CombinedReservation_WhenTotalBudgetExhausted_DoesNotAcquireSlot()
    {
        var ledger = new RunBudgetLedger(Budget(subagents: 0));

        Assert.False(ledger.TryReserveSubagentWithSlot());
        Assert.Equal(0, ledger.Remaining.MaxTotalAgents);
        Assert.False(ledger.ReleaseActiveChild());
    }

    [Fact]
    public void ToolAndModelCalls_RespectIndependentCaps()
    {
        var ledger = new RunBudgetLedger(Budget());

        for (var i = 0; i < 20; i++) Assert.True(ledger.TryReserveToolCall());
        Assert.False(ledger.TryReserveToolCall());
        for (var i = 0; i < 12; i++) Assert.True(ledger.TryReserveModelCall(1));
        Assert.False(ledger.TryReserveModelCall(1));
        Assert.Equal(0, ledger.Remaining.MaxToolCalls);
        Assert.Equal(0, ledger.Remaining.MaxModelCalls);
        Assert.Equal(88, ledger.Remaining.MaxTokens);
    }

    [Fact]
    public void FailedCombinedModelReservation_DoesNotPartiallyDebit()
    {
        var ledger = new RunBudgetLedger(Budget(models: 1, tokens: 5));

        Assert.False(ledger.TryReserveModelCall(6));
        Assert.Equal(1, ledger.Remaining.MaxModelCalls);
        Assert.Equal(5, ledger.Remaining.MaxTokens);
    }

    [Fact]
    public void ChildBudgetIsNarrowedAndSubagentReservationIsDebitedOnlyOnce()
    {
        var parent = Budget(subagents: 8, tools: 20, models: 12, tokens: 100);
        var child = new AgentBudget(4, 3, 25, TimeSpan.FromSeconds(20), 1, 2, 2);
        var ledger = new RunBudgetLedger(parent, child);

        Assert.True(ledger.TryReserveSubagent(1, 0));
        var remaining = ledger.Remaining;
        Assert.Equal(1, remaining.MaxTotalAgents);
        Assert.Equal(4, remaining.MaxToolCalls);
        Assert.Equal(3, remaining.MaxModelCalls);
        Assert.Equal(25, remaining.MaxTokens);
    }

    [Fact]
    public void ReservationsNeverAllowNegativeBudgets()
    {
        var ledger = new RunBudgetLedger(Budget(subagents: 1, tools: 1, models: 1, tokens: 1));

        Assert.True(ledger.TryReserveSubagent(1, 0));
        Assert.True(ledger.TryReserveToolCall());
        Assert.True(ledger.TryReserveModelCall(1));
        Assert.False(ledger.TryReserveTokens(long.MaxValue));
        Assert.Equal(0, ledger.Remaining.MaxTotalAgents);
        Assert.Equal(0, ledger.Remaining.MaxToolCalls);
        Assert.Equal(0, ledger.Remaining.MaxModelCalls);
        Assert.Equal(0, ledger.Remaining.MaxTokens);
    }

    [Fact]
    public async Task AdaptersSharingLedger_EnforceConcurrentLimitAndReleaseAfterCompletion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enteredCount = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new Registry(new AgentDefinition(new AgentId("child"), "child"));
        var client = new Client(async (_, token) =>
        {
            if (Interlocked.Increment(ref enteredCount) == 3) entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return "done";
        });
        var factory = new MafRealAgentFactory(registry, client);
        var runner = new MafConversationRunner(factory, new AgentId("lead"));
        var execution = new ExecutionId(Ulid.NewUlid());
        var context = new AgentExecutionContext
        {
            TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()), AgentExecutionId = execution,
            Depth = 0, CatalogRevision = 1, PolicyRevision = 1, AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty.Add("MaxToolCalls", 1).Add("MaxModelCalls", 1)
                .Add("MaxTokens", 100).Add("MaxConcurrent", 1).Add("MaxQueued", 2).Add("MaxTotal", 10)
                .Add("MaxRuntimeTicks", long.MaxValue), CancellationToken = timeout.Token
        };
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        OrchestrationAdapters MakeAdapter() => new(factory, runner, (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), 2, ledger);
        var parent = ToTraceId(execution);
        var grant = new DelegationGrant(new AgentId("lead"), new AgentId("child"), 1, new HashSet<ToolId>(),
            new HashSet<string> { "scope" }, new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 1, 2, 10), parent, "policy");
        var first = Task.WhenAll(Enumerable.Range(0, 3).Select(_ => MakeAdapter().SequentialRunner(execution, [grant], "run", context)));
        await entered.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MakeAdapter().SequentialRunner(execution, [grant], "run", context));
        finish.TrySetResult();
        await first.WaitAsync(timeout.Token);
        Assert.True(ledger.TryAcquireActiveChild());
        Assert.True(ledger.ReleaseActiveChild());
        Assert.False(ledger.ReleaseActiveChild());
    }

    private static Guid ToTraceId(ExecutionId id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed class Registry(params AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(item => item.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class Client(Func<IEnumerable<ChatMessage>, CancellationToken, Task<string>> respond) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("ledger-test");
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => new(new ChatMessage(ChatRole.Assistant, await respond(messages, cancellationToken)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, (await GetResponseAsync(messages, options, cancellationToken)).Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
