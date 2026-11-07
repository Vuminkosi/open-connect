using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafRealAgentTests
{
    [Fact]
    public async Task Real_maf_agent_runs_one_turn_and_records_parent_child_trace()
    {
        var client = new TestChatClient();
        var id = new AgentId("echo");
        var registry = new TestRegistry(new AgentDefinition(id, "Echo"));
        var factory = new MafRealAgentFactory(registry, client);

        var response = await factory.RunAsync(id, "hello");
        var trace = factory.GetTrace();

        Assert.Equal("hello", response.Text);
        Assert.Equal(1, client.Calls);
        Assert.Equal(2, trace.Count);
        Assert.NotEqual(Guid.Empty, trace[0].Id);
        Assert.Null(trace[0].ParentId);
        Assert.Equal(trace[0].Id, trace[1].ParentId);
        Assert.Equal("echo", trace[1].AgentId);
        Assert.NotNull(trace[0].CompletedAt);
        Assert.NotNull(trace[1].CompletedAt);
    }

    private sealed class TestChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var text = messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
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
