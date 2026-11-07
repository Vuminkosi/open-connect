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
    public async Task Operational_definition_executes_through_same_maf_adapter_with_distinct_principal()
    {
        var client = new TestChatClient();
        var interactiveId = new AgentId("interactive-echo");
        var operationalId = new AgentId("operational-echo");
        const string prompt = "hello across modes";
        var interactiveDefinition = new AgentDefinition(interactiveId, "Echo");
        var operationalDefinition = new AgentDefinition(operationalId, "Echo")
        {
            ExecutionMode = AgentExecutionMode.Operational,
            IdentityMode = AgentIdentityMode.WorkloadIdentity,
            Operational = new OperationalPolicy(
                AgentActivationMode.OnDemand,
                WorkloadIdentityRef: "test/workload")
        };
        var factory = new MafRealAgentFactory(new TestRegistry(interactiveDefinition, operationalDefinition), client);

        var interactiveResponse = await factory.RunAsync(interactiveId, prompt);
        var operationalResponse = await factory.RunAsync(operationalId, prompt);
        var trace = factory.GetTrace();

        Assert.Equal(prompt, interactiveResponse.Text);
        Assert.Equal(prompt, operationalResponse.Text);
        Assert.Equal(2, client.Calls);
        Assert.Equal(2, client.CapturedCalls.Count);

        var interactiveCall = client.CapturedCalls[0];
        var operationalCall = client.CapturedCalls[1];
        Assert.Equal(interactiveCall.Messages.Select(message => (message.Role, message.Text)),
            operationalCall.Messages.Select(message => (message.Role, message.Text)));
        Assert.Collection(interactiveCall.Messages,
            message =>
            {
                Assert.Equal(ChatRole.User, message.Role);
                Assert.Equal(prompt, message.Text);
            });
        Assert.Collection(operationalCall.Messages,
            message =>
            {
                Assert.Equal(ChatRole.User, message.Role);
                Assert.Equal(prompt, message.Text);
            });
        Assert.Equal(interactiveCall.Options?.Instructions, operationalCall.Options?.Instructions);
        Assert.Equal("Echo", interactiveCall.Options?.Instructions);
        Assert.Contains(trace, record => record.AgentId == operationalId.Value);
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
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> CapturedCalls { get; } = [];
        public ChatOptions? CapturedOptions { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var capturedMessages = messages.ToArray();
            CapturedCalls.Add((capturedMessages, options));
            CapturedOptions = options;
            var text = capturedMessages.Last(message => message.Role == ChatRole.User).Text;
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

    private sealed class TestRegistry(params AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(definition => id == definition.Id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }
}
