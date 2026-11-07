using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafSessionSerializationTests
{
    [Fact]
    public async Task Round_trip_preserves_history_when_resumed()
    {
        var client = new StubChatClient();
        var agent = new MafRealAgentFactory(new Registry(), client).CreateAgent(new AgentId("actor"));
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("first turn", session);
        var serialized = await agent.SerializeSessionAsync(session);
        var restored = await agent.DeserializeSessionAsync(serialized);
        await agent.RunAsync("second turn", restored);

        Assert.Contains(client.Requests.Last(), message => message.Role == ChatRole.User && message.Text == "first turn");
        Assert.Equal("second turn", client.Requests.Last().Last(message => message.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task Deserialized_session_is_isolated_from_another_session()
    {
        var client = new StubChatClient();
        var agent = new MafRealAgentFactory(new Registry(), client).CreateAgent(new AgentId("actor"));
        var original = await agent.CreateSessionAsync();
        var other = await agent.CreateSessionAsync();
        await agent.RunAsync("original only", original);
        await agent.RunAsync("other only", other);

        var restored = await agent.DeserializeSessionAsync(await agent.SerializeSessionAsync(original));
        await agent.RunAsync("restored follow-up", restored);

        var restoredRequest = client.Requests.Last();
        Assert.Contains(restoredRequest, message => message.Role == ChatRole.User && message.Text == "original only");
        Assert.DoesNotContain(restoredRequest, message => message.Role == ChatRole.User && message.Text == "other only");
    }

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("actor"), "Actor");
        public AgentDefinition? Get(AgentId id) => Definition;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class StubChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("probe");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.ToArray();
            Requests.Add(request);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, request.Last(message => message.Role == ChatRole.User).Text)));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { var response = await GetResponseAsync(messages, options, cancellationToken); yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text); }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
