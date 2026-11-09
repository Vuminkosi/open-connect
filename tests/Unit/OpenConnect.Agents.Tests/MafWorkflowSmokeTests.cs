using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafWorkflowSmokeTests
{
    [Fact]
    public async Task Real_agent_sessions_keep_actor_histories_isolated()
    {
        var client = new RecordingChatClient();
        var agent = CreateFactory(client).CreateAgent(new AgentId("actor"));
        var firstSession = await agent.CreateSessionAsync();
        var secondSession = await agent.CreateSessionAsync();

        await agent.RunAsync("first actor message", firstSession);
        await agent.RunAsync("second actor message", secondSession);
        await agent.RunAsync("first actor follow-up", firstSession);

        Assert.Equal("first actor message", client.Requests[0].Single(message => message.Role == ChatRole.User).Text);
        Assert.Equal("second actor message", client.Requests[1].Single(message => message.Role == ChatRole.User).Text);
        Assert.Contains(client.Requests[2], message => message.Role == ChatRole.User && message.Text == "first actor message");
        Assert.DoesNotContain(client.Requests[2], message => message.Role == ChatRole.User && message.Text == "second actor message");
        Assert.Equal("first actor follow-up", client.Requests[2].Last(message => message.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task Real_agent_session_preserves_turn_history()
    {
        var client = new RecordingChatClient();
        var agent = CreateFactory(client).CreateAgent(new AgentId("actor"));
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("first turn", session);
        var result = await agent.RunAsync("second turn", session);

        Assert.Equal("second turn", result.Text);
        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.User && message.Text == "first turn");
        Assert.Contains(client.Requests[1], message => message.Role == ChatRole.Assistant && message.Text == "first turn");
        Assert.Equal("second turn", client.Requests[1].Last(message => message.Role == ChatRole.User).Text);
    }

    private static MafRealAgentFactory CreateFactory(RecordingChatClient client) =>
        new(new TestRegistry(), client);

    private sealed class TestRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("actor"), "Actor");

        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("session-smoke");

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
