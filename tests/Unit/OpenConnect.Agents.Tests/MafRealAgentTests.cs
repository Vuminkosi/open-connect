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

    [Fact]
    public async Task Instructions_reach_maf_agent_as_system_guidance()
    {
        var client = new TestChatClient();
        var id = new AgentId("echo");
        var factory = new MafRealAgentFactory(new TestRegistry(new AgentDefinition(id, "Echo")), client);

        await factory.RunAsync(id, "hello", tools: null, instructions: "You are a test pirate");

        Assert.Equal("You are a test pirate", client.CapturedOptions?.Instructions);
    }

    [Fact]
    public async Task Default_instructions_remain_definition_name()
    {
        var client = new TestChatClient();
        var id = new AgentId("echo");
        var factory = new MafRealAgentFactory(new TestRegistry(new AgentDefinition(id, "Echo")), client);

        await factory.RunAsync(id, "hello");

        Assert.Equal("Echo", client.CapturedOptions?.Instructions);
    }

    [Fact]
    public void UnknownAgent_with_instructions_still_throws_before_maf_call()
    {
        var client = new TestChatClient();
        var id = new AgentId("echo");
        var factory = new MafRealAgentFactory(new TestRegistry(new AgentDefinition(id, "Echo")), client);

        Assert.Throws<InvalidOperationException>(() => factory.CreateAgent(new AgentId("missing"), instructions: "You are a test pirate"));
        Assert.Equal(0, client.Calls);
    }

    private sealed class TestChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public List<ChatMessage> CapturedMessages { get; } = [];
        public ChatOptions? CapturedOptions { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            CapturedMessages.AddRange(messages);
            CapturedOptions = options;
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
