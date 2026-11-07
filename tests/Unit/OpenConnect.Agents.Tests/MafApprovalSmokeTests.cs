using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MafApprovalSmokeTests
{
    [Fact]
    public async Task Approval_required_function_is_surfaced_without_execution()
    {
        var client = new ApprovalCallClient();
        var factory = new MafRealAgentFactory(
            new TestRegistry(new AgentDefinition(new AgentId("approval"), "Approval")), client);
        var executions = 0;
        var function = AIFunctionFactory.Create(() =>
        {
            executions++;
            return "completed";
        }, "sensitive_action");
        var guarded = new ApprovalRequiredAIFunction(function);

        var response = await factory.RunAsync(new AgentId("approval"), "run action", [guarded]);

        Assert.Equal(0, executions);
        Assert.Contains(response.Messages.SelectMany(message => message.Contents),
            content => content is ToolApprovalRequestContent);
    }

    private sealed class ApprovalCallClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
            var call = new FunctionCallContent("approval-call", function.Name, new Dictionary<string, object?>());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
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
