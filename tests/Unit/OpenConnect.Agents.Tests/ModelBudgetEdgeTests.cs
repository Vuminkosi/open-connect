using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ModelBudgetEdgeTests
{
    [Fact]
    public async Task TurnAsync_exhaustion_does_not_call_model_or_mutate_history_or_trace()
    {
        var client = new Client();
        var ledger = Ledger(models: 0);
        var runner = Runner(client, ledger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnAsync("lead"));

        Assert.Equal(0, client.Calls);
        Assert.Empty(runner.History);
        Assert.Empty(runner.GetTrace());
        Assert.Equal(0, ledger.Remaining.MaxModelCalls);
    }

    [Fact]
    public async Task DelegateAsync_exhaustion_does_not_create_agent_call_or_trace()
    {
        var client = new Client();
        var runner = Runner(client, Ledger(models: 0));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.DelegateAsync(Guid.NewGuid(), new AgentId("agent"), "child"));

        Assert.Equal(0, client.Calls);
        Assert.Empty(runner.GetTrace());
    }

    [Fact]
    public async Task DirectRunner_exhaustion_is_checked_after_authorization_and_before_model_call()
    {
        var client = new Client();
        var ledger = Ledger(models: 0);
        var (adapters, context) = Adapters(client, ledger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapters.DirectRunner(new AgentId("agent"), "direct", context));

        Assert.Equal(0, client.Calls);
        Assert.Equal(0, ledger.Remaining.MaxModelCalls);
    }

    [Fact]
    public async Task Each_successful_real_invocation_debits_exactly_once()
    {
        var client = new Client();
        var ledger = Ledger(models: 3, tokens: 100);
        var runner = Runner(client, ledger);
        await runner.TurnAsync("lead");
        await runner.DelegateAsync(runner.GetTrace()[0].Id, new AgentId("agent"), "child");
        var (adapters, context) = Adapters(client, ledger);
        await adapters.DirectRunner(new AgentId("agent"), "direct", context);

        Assert.Equal(0, ledger.Remaining.MaxModelCalls);
        Assert.Equal(100 - Encoding.UTF8.GetByteCount("lead") - Encoding.UTF8.GetByteCount("child") - Encoding.UTF8.GetByteCount("direct"), ledger.Remaining.MaxTokens);
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task Revoked_direct_attempt_does_not_consume_model_budget()
    {
        var client = new Client();
        var ledger = Ledger(models: 1, tokens: 100);
        var (adapters, context) = Adapters(client, ledger, revoked: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapters.DirectRunner(new AgentId("agent"), "direct", context));

        Assert.Equal(1, ledger.Remaining.MaxModelCalls);
        Assert.Equal(100, ledger.Remaining.MaxTokens);
        Assert.Equal(0, client.Calls);
    }

    private static RunBudgetLedger Ledger(int models, long tokens = 100) =>
        new(new AgentBudget(10, models, tokens, TimeSpan.FromMinutes(1), 2, 2, 2));

    private static MafConversationRunner Runner(Client client, RunBudgetLedger ledger) =>
        new(new MafRealAgentFactory(new Registry(), client), new AgentId("lead"), runLedger: ledger);

    private static (OrchestrationAdapters Adapters, AgentExecutionContext Context) Adapters(Client client,
        RunBudgetLedger ledger, bool revoked = false)
    {
        var factory = new MafRealAgentFactory(new Registry(), client);
        var context = new AgentExecutionContext
        {
            TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1,
            PolicyRevision = 1, AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty, CancellationToken = CancellationToken.None
        };
        var runner = new MafConversationRunner(factory, new AgentId("lead"), runLedger: ledger);
        return (new OrchestrationAdapters(factory, runner, (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(revoked), 1, ledger), context);
    }

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition[] Definitions = [new(new AgentId("lead"), "Lead"), new(new AgentId("agent"), "Agent")];
        public AgentDefinition? Get(AgentId id) => Definitions.FirstOrDefault(item => item.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => Definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class Client : IChatClient
    {
        public int Calls { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("model-budget-edge-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, messages.Last().Text)));
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
}
