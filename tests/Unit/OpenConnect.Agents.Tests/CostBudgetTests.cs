using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class CostBudgetTests
{
    private static AgentBudget Budget(long cost = long.MaxValue, int models = 10, long tokens = 100_000) =>
        new(10, models, tokens, TimeSpan.FromMinutes(1), 3, 4, 10, cost);

    [Fact]
    public void TryReserveCost_Negative_ReturnsFalseWithoutDebit()
    {
        var ledger = new RunBudgetLedger(Budget(cost: 5));

        Assert.False(ledger.TryReserveCost(-1));
        Assert.Equal(5, ledger.Remaining.MaxCostMicrocents);
    }

    [Fact]
    public void TryReserveCost_Denied_WhenExhausted_LeavesRemainingUnchanged()
    {
        var ledger = new RunBudgetLedger(Budget(cost: 5));

        Assert.True(ledger.TryReserveCost(3));
        Assert.Equal(2, ledger.Remaining.MaxCostMicrocents);
        Assert.False(ledger.TryReserveCost(3));
        Assert.Equal(2, ledger.Remaining.MaxCostMicrocents);
    }

    [Fact]
    public void TryReserveModelCall_DebitsCostAsCeilTokensTimesRateOverOneThousand()
    {
        var ledger = new RunBudgetLedger(Budget(cost: 10_000), microcentsPerThousandTokens: 1_500);

        Assert.True(ledger.TryReserveModelCall(1_001));
        Assert.Equal(10_000 - 1_502, ledger.Remaining.MaxCostMicrocents);
    }

    [Fact]
    public void TryReserveModelCall_CostDenied_LeavesAllCountersUnchanged()
    {
        var ledger = new RunBudgetLedger(Budget(cost: 1), microcentsPerThousandTokens: 1_000);

        Assert.False(ledger.TryReserveModelCall(2));
        var remaining = ledger.Remaining;
        Assert.Equal(10, remaining.MaxModelCalls);
        Assert.Equal(100_000, remaining.MaxTokens);
        Assert.Equal(1, remaining.MaxCostMicrocents);
    }

    [Fact]
    public void TryReserveModelCall_ZeroRate_NeverDebitsCost()
    {
        var ledger = new RunBudgetLedger(Budget());

        Assert.True(ledger.TryReserveModelCall(1_000));
        Assert.Equal(long.MaxValue, ledger.Remaining.MaxCostMicrocents);
        Assert.Equal(100_000 - 1_000, ledger.Remaining.MaxTokens);
    }

    [Fact]
    public void TryReserveModelCall_CostOverflow_DeniesFailClosedWithoutDebit()
    {
        var ledger = new RunBudgetLedger(Budget(tokens: long.MaxValue), microcentsPerThousandTokens: long.MaxValue);

        Assert.False(ledger.TryReserveModelCall(long.MaxValue));
        var remaining = ledger.Remaining;
        Assert.Equal(10, remaining.MaxModelCalls);
        Assert.Equal(long.MaxValue, remaining.MaxTokens);
        Assert.Equal(long.MaxValue, remaining.MaxCostMicrocents);
    }

    [Fact]
    public void Constructor_NegativeRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RunBudgetLedger(Budget(), microcentsPerThousandTokens: -1));
    }

    [Fact]
    public async Task TurnAsync_CostCapDenial_HappensBeforeMafInvokeWithNoTraceOrHistory()
    {
        var client = new TestChatClient();
        var ledger = new RunBudgetLedger(Budget(cost: 1), microcentsPerThousandTokens: 1_000);
        var runner = new MafConversationRunner(
            new MafRealAgentFactory(new TestRegistry(new AgentDefinition(new AgentId("echo"), "Echo")), client),
            new AgentId("echo"), runLedger: ledger);
        var before = ledger.Remaining;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnAsync("hi"));

        Assert.Equal("run-wide model budget exhausted", error.Message);
        Assert.Equal(0, client.Calls);
        Assert.Empty(runner.History);
        Assert.Empty(runner.GetTrace());
        Assert.Equal(before, ledger.Remaining);
    }

    private sealed class TestChatClient : IChatClient
    {
        private int _calls;
        public int Calls => _calls;
        public ChatClientMetadata Metadata { get; } = new("cost-budget-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                messages.Last(message => message.Role == ChatRole.User).Text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                (await GetResponseAsync(messages, options, cancellationToken)).Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestRegistry(AgentDefinition definition) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => id == definition.Id ? definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }
}
