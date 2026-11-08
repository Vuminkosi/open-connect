using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ChildSessionContinuityTests
{
    [Fact]
    public async Task Same_key_continues_child_history()
    {
        var (runner, client, child) = CreateRunner("child");

        await runner.DelegateAsync(Guid.NewGuid(), child, "first marker", childSessionKey: "branch-a");
        await runner.DelegateAsync(Guid.NewGuid(), child, "second marker", childSessionKey: "branch-a");

        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.User && message.Text == "first marker");
        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.Assistant && message.Text == "first marker");
        Assert.Equal("second marker", client.Requests[1].Last(message => message.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task Different_keys_keep_histories_isolated()
    {
        var (runner, client, child) = CreateRunner("child");

        await runner.DelegateAsync(Guid.NewGuid(), child, "private-a", childSessionKey: "a");
        await runner.DelegateAsync(Guid.NewGuid(), child, "private-b", childSessionKey: "b");

        Assert.DoesNotContain(client.Requests[1], message => message.Text == "private-a");
        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.User && message.Text == "private-b");
    }

    [Fact]
    public async Task Different_children_keep_histories_isolated()
    {
        var (runner, client, firstChild) = CreateRunner("child-a", "child-b");
        var secondChild = new AgentId("child-b");

        await runner.DelegateAsync(Guid.NewGuid(), firstChild, "from-a", childSessionKey: "shared-key");
        await runner.DelegateAsync(Guid.NewGuid(), secondChild, "from-b", childSessionKey: "shared-key");

        Assert.DoesNotContain(client.Requests[1], message => message.Text == "from-a");
        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.User && message.Text == "from-b");
    }

    [Fact]
    public async Task Null_key_preserves_prompt_only_legacy_behavior()
    {
        var (runner, client, child) = CreateRunner("child");

        await runner.DelegateAsync(Guid.NewGuid(), child, "legacy one");
        await runner.DelegateAsync(Guid.NewGuid(), child, "legacy two", childSessionKey: string.Empty);

        Assert.Single(client.Requests[0]);
        Assert.Single(client.Requests[1]);
        Assert.Equal("legacy one", client.Requests[0][0].Text);
        Assert.Equal("legacy two", client.Requests[1][0].Text);
    }

    [Fact]
    public async Task Whitespace_key_rejected_before_any_trace()
    {
        var (runner, _, child) = CreateRunner("child");
        var parentExecutionId = Guid.NewGuid();
        var traceCount = runner.GetTrace().Count;

        await Assert.ThrowsAsync<ArgumentException>(() => runner
            .DelegateAsync(parentExecutionId, child, "prompt", childSessionKey: " ")
            .WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal(traceCount, runner.GetTrace().Count);
        Assert.DoesNotContain(runner.GetTrace(), trace => trace.ParentId == parentExecutionId);
    }

    [Fact]
    public async Task Exhausted_budget_leaves_no_child_trace()
    {
        var client = new RecordingChatClient();
        var factory = new MafRealAgentFactory(new TestRegistry([new AgentDefinition(new AgentId("child"), "child")]), client);
        var ledger = new RunBudgetLedger(new AgentBudget(10, 0, 100, TimeSpan.FromMinutes(1), 2, 2, 2));
        var runner = new MafConversationRunner(factory, new AgentId("lead"), runLedger: ledger);
        var parentExecutionId = Guid.NewGuid();
        var traceCount = runner.GetTrace().Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner
            .DelegateAsync(parentExecutionId, new AgentId("child"), "prompt", childSessionKey: "branch")
            .WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal(traceCount, runner.GetTrace().Count);
        Assert.DoesNotContain(runner.GetTrace(), trace => trace.ParentId == parentExecutionId);
        Assert.Empty(client.Requests);
    }

    private static (MafConversationRunner Runner, RecordingChatClient Client, AgentId Child) CreateRunner(params string[] childIds)
    {
        var client = new RecordingChatClient();
        var definitions = childIds.Select(id => new AgentDefinition(new AgentId(id), id)).ToArray();
        var factory = new MafRealAgentFactory(new TestRegistry(definitions), client);
        return (new MafConversationRunner(factory, new AgentId("lead")), client, new AgentId(childIds[0]));
    }

    private sealed class TestRegistry(AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(definition => definition.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("child-session-continuity");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.ToArray();
            Requests.Add(request);
            var text = request.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
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
