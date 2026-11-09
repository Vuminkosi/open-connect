using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafLeadSessionTests
{
    [Fact]
    public async Task Lead_turns_continue_in_native_session_without_manual_history_replay()
    {
        var client = new RecordingChatClient();
        var runner = CreateRunner(client);

        await runner.TurnAsync("first");
        await runner.TurnAsync("second");

        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.User && message.Text == "first");
        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.Assistant && message.Text == "first");
        Assert.Equal("second", client.Requests[1].Last(message => message.Role == ChatRole.User).Text);
        Assert.Equal(new[] { "first", "second" }, runner.History.Where(message => message.Role == ChatRole.User).Select(message => message.Text));
    }

    [Fact]
    public async Task Lead_sessions_are_isolated_between_runners()
    {
        var client = new RecordingChatClient();
        var first = CreateRunner(client);
        var second = CreateRunner(client);

        await first.TurnAsync("private to first");
        await second.TurnAsync("private to second");
        await first.TurnAsync("follow up");

        Assert.Contains(client.Requests[2], message => message.Text == "private to first");
        Assert.DoesNotContain(client.Requests[2], message => message.Text == "private to second");
    }

    [Fact]
    public async Task Failed_first_turn_does_not_prevent_a_clean_successful_turn()
    {
        var client = new RecordingChatClient { FailPrompt = "failed first" };
        var runner = CreateRunner(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnAsync("failed first"));
        await runner.TurnAsync("successful retry");
        await runner.TurnAsync("follow up");

        Assert.DoesNotContain(client.Requests[1], message => message.Text == "failed first");
        Assert.Contains(client.Requests[2], message => message.Text == "successful retry");
        Assert.DoesNotContain(client.Requests[2], message => message.Text == "failed first");
        Assert.Equal(new[] { "successful retry", "successful retry", "follow up", "follow up" }, runner.History.Select(message => message.Text));
    }

    [Fact]
    public async Task Failed_or_cancelled_candidate_does_not_contaminate_committed_session()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new RecordingChatClient { FailPrompt = "failed continuation", CancelPrompt = "cancelled continuation", Cancellation = cancellation };
        var runner = CreateRunner(client);
        await runner.TurnAsync("committed turn");

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnAsync("failed continuation"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.TurnAsync("cancelled continuation", cancellation.Token));

        cancellation.Dispose();
        using var nextCancellation = new CancellationTokenSource();
        client.Cancellation = nextCancellation;
        client.CancelPrompt = null;
        await runner.TurnAsync("clean continuation", nextCancellation.Token);

        var lastRequest = client.Requests[^1];
        Assert.Contains(lastRequest, message => message.Text == "committed turn");
        Assert.DoesNotContain(lastRequest, message => message.Text == "failed continuation");
        Assert.DoesNotContain(lastRequest, message => message.Text == "cancelled continuation");
        Assert.Contains(lastRequest, message => message.Text == "clean continuation");
        Assert.Equal(new[] { "committed turn", "committed turn", "clean continuation", "clean continuation" }, runner.History.Select(message => message.Text));
    }

    private static MafConversationRunner CreateRunner(RecordingChatClient client) =>
        new(new MafRealAgentFactory(new Registry(), client), new AgentId("lead"));

    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public string? FailPrompt { get; init; }
        public string? CancelPrompt { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.ToArray();
            Requests.Add(request);
            var latest = request.Last(message => message.Role == ChatRole.User).Text;
            if (latest == FailPrompt) throw new InvalidOperationException("expected test failure");
            if (latest == CancelPrompt) Cancellation!.Cancel();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, latest)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("lead"), "Lead");
        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }
}
