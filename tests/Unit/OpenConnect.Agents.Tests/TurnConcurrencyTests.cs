using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class TurnConcurrencyTests
{
    [Fact]
    public async Task Serialize_allows_overlapping_turns_to_complete_in_order()
    {
        await AssertSerializedAsync(SessionTurnConcurrency.Serialize);
    }

    [Fact]
    public async Task RejectConflict_throws_immediately_when_turn_active()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new GatedChatClient(async (messages, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return messages.Last(m => m.Role == ChatRole.User).Text;
        });
        var runner = CreateRunner(client, SessionTurnConcurrency.RejectConflict);
        var first = runner.TurnAsync("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnAsync("second"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "first" }, runner.History.Select(m => m.Text));
        Assert.Equal(1, client.Calls);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task RejectConflict_allows_turn_after_completion()
    {
        var client = new GatedChatClient();
        var runner = CreateRunner(client, SessionTurnConcurrency.RejectConflict);

        await runner.TurnAsync("first");
        await runner.TurnAsync("second");

        Assert.Equal(2, client.Calls);
        Assert.Equal(new[] { "first", "first", "second", "second" }, runner.History.Select(m => m.Text));
    }

    [Fact]
    public async Task Default_is_Serialize()
    {
        await AssertSerializedAsync(null);
    }

    private static async Task AssertSerializedAsync(SessionTurnConcurrency? policy)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new GatedChatClient(async (messages, token) =>
        {
            var prompt = messages.Last(m => m.Role == ChatRole.User).Text;
            if (prompt == "first")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return prompt;
        });
        var runner = CreateRunner(client, policy);
        var first = runner.TurnAsync("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = runner.TurnAsync("second");
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(new[] { "first", "first", "second", "second" }, runner.History.Select(m => m.Text));
        Assert.Equal(2, client.Calls);
    }

    private static MafConversationRunner CreateRunner(GatedChatClient client, SessionTurnConcurrency? concurrency = null)
    {
        var factory = new MafRealAgentFactory(new TestRegistry(), client);
        return concurrency is null
            ? new MafConversationRunner(factory, new AgentId("echo"))
            : new MafConversationRunner(factory, new AgentId("echo"), concurrency.Value);
    }

    private sealed class GatedChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<string>>? respond = null) : IChatClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ChatClientMetadata Metadata { get; } = new("test");

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            cancellationToken.ThrowIfCancellationRequested();
            var text = respond is null ? messages.Last(m => m.Role == ChatRole.User).Text : await respond(messages, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("echo"), "Echo");
        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }
}
