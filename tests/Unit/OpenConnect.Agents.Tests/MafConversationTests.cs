using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafConversationTests
{
    [Fact]
    public async Task TurnAsync_preserves_history_across_two_real_agent_turns()
    {
        var client = new TestChatClient();
        var runner = CreateRunner(client);

        await runner.TurnAsync("first");
        await runner.TurnAsync("second");

        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant }, runner.History.Select(message => message.Role));
        Assert.Equal("second", runner.History[^2].Text);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task DelegateAsync_records_parent_child_ids_for_actual_child_call()
    {
        var client = new TestChatClient();
        var runner = CreateRunner(client);
        await runner.TurnAsync("lead");
        var parentId = runner.GetTrace().Single().Id;

        var response = await runner.DelegateAsync(parentId, new AgentId("echo"), "child request");
        var trace = runner.GetTrace();

        Assert.Equal("child request", response.Text);
        Assert.Equal(2, client.Calls);
        Assert.Equal(parentId, trace[1].ParentId);
        Assert.NotEqual(trace[0].Id, trace[1].Id);
        Assert.Equal("echo", trace[1].AgentId);
        Assert.All(trace, record => Assert.NotNull(record.CompletedAt));
    }

    [Fact]
    public async Task Overlapping_turn_failure_removes_only_failed_prompt_and_keeps_order()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestChatClient(async (messages, cancellationToken) =>
        {
            var prompt = messages.Last(message => message.Role == ChatRole.User).Text;
            if (prompt == "first")
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("expected test failure");
            }

            return prompt;
        });
        var runner = CreateRunner(client);
        var first = runner.TurnAsync("first");
        await entered.Task;
        var second = runner.TurnAsync("second");
        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var response = await second;

        Assert.Equal("second", response.Text);
        Assert.Equal(new[] { "second", "second" }, runner.History.Select(message => message.Text));
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task Waiting_turn_cancellation_does_not_append_to_history()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestChatClient(async (messages, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return messages.Last(message => message.Role == ChatRole.User).Text;
        });
        var runner = CreateRunner(client);
        var first = runner.TurnAsync("first");
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var second = runner.TurnAsync("cancelled", cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        await first;

        Assert.Equal(new[] { "first", "first" }, runner.History.Select(message => message.Text));
        Assert.Equal(1, client.Calls);
    }

    private static MafConversationRunner CreateRunner(TestChatClient client) =>
        new(new MafRealAgentFactory(new TestRegistry(new AgentDefinition(new AgentId("echo"), "Echo")), client), new AgentId("echo"));

    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<string>>? respond = null) : IChatClient
    {
        private int _calls;
        public int Calls => _calls;
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return GetResponseCoreAsync(messages, cancellationToken);
        }

        private async Task<ChatResponse> GetResponseCoreAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = respond is null
                ? messages.Last(message => message.Role == ChatRole.User).Text
                : await respond(messages, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
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
