using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class DelegationAccountingTests
{
    [Fact]
    public async Task Delegated_model_attempt_is_charged_once_and_excess_attempt_is_denied_without_debit()
    {
        const string prompt = "child";
        var client = new RecordingChatClient();
        var childId = new AgentId("child");
        var factory = new MafRealAgentFactory(new TestRegistry(new AgentDefinition(childId, "child")), client);
        var ledger = new RunBudgetLedger(new AgentBudget(2, 2, 5, TimeSpan.FromMinutes(1), 1, 1, 1));
        var runner = new MafConversationRunner(factory, new AgentId("parent"), runLedger: ledger);
        using var parent = CreateParentContext();
        using var child = parent.CreateChild("tenant", "principal",
            ImmutableDictionary<string, long>.Empty.Add("MaxModelCalls", 2).Add("MaxTokens", 5), CancellationToken.None);

        await runner.DelegateAsync(Guid.NewGuid(), childId, prompt, child.CancellationToken);

        Assert.Equal(1, ledger.Remaining.MaxModelCalls);
        Assert.Equal(0, ledger.Remaining.MaxTokens);
        Assert.Single(client.Requests);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.DelegateAsync(Guid.NewGuid(), childId, "x", child.CancellationToken));

        Assert.Equal(1, ledger.Remaining.MaxModelCalls);
        Assert.Equal(0, ledger.Remaining.MaxTokens);
        Assert.Single(client.Requests);
    }

    private static AgentExecutionContext CreateParentContext() => new()
    {
        TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1,
        PolicyRevision = 1, AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        RemainingBudgets = ImmutableDictionary<string, long>.Empty.Add("MaxModelCalls", 2).Add("MaxTokens", 5),
        CancellationToken = CancellationToken.None
    };

    private sealed class TestRegistry(AgentDefinition definition) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => id == definition.Id ? definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("delegation-accounting-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.ToArray();
            Requests.Add(request);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
