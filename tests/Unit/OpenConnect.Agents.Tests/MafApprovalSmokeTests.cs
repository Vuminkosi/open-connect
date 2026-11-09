using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
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

    [Fact]
    public async Task Approval_response_resumes_paused_tool_call()
    {
        var client = new ApprovalResumeClient();
        var factory = new MafRealAgentFactory(
            new TestRegistry(new AgentDefinition(new AgentId("approval"), "Approval")), client);
        var executions = 0;
        var function = AIFunctionFactory.Create(() =>
        {
            executions++;
            return "function output";
        }, "sensitive_action");
        var agent = factory.CreateAgent(new AgentId("approval"), [new ApprovalRequiredAIFunction(function)]);
        var session = await agent.CreateSessionAsync();

        var paused = await agent.RunAsync("run action", session);
        var request = Assert.IsType<ToolApprovalRequestContent>(
            Assert.Single(paused.Messages.SelectMany(message => message.Contents)));

        Assert.Equal(0, executions);
        Assert.DoesNotContain(paused.Messages.SelectMany(message => message.Contents),
            content => content is FunctionResultContent);

        // The stub is standing in for the Tools/host approval decision; production approval policy lives there.
        var approvalResponse = request.CreateResponse(approved: true);
        var resumed = await agent.RunAsync(new ChatMessage(ChatRole.User, [approvalResponse]), session);

        Assert.Equal(1, executions);
        Assert.Contains("function output", resumed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declined_approval_skips_tool_execution_without_error()
    {
        var client = new ApprovalResumeClient();
        var factory = new MafRealAgentFactory(
            new TestRegistry(new AgentDefinition(new AgentId("approval"), "Approval")), client);
        var executions = 0;
        var function = AIFunctionFactory.Create(() =>
        {
            executions++;
            return "function output";
        }, "sensitive_action");
        var agent = factory.CreateAgent(new AgentId("approval"), [new ApprovalRequiredAIFunction(function)]);
        var session = await agent.CreateSessionAsync();
        var paused = await agent.RunAsync("run action", session).WaitAsync(TimeSpan.FromSeconds(5));
        var request = Assert.IsType<ToolApprovalRequestContent>(
            Assert.Single(paused.Messages.SelectMany(message => message.Contents)));
        var declined = await agent.RunAsync(
            new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false)]), session)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, executions);
        Assert.NotNull(declined);
        // A declined tool's output must neither execute nor appear in the answer.
        Assert.DoesNotContain("Finished: function output", declined.Text, StringComparison.Ordinal);
    }

    private sealed class ApprovalResumeClient : IChatClient
    {
        private int _calls;
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
                var call = new FunctionCallContent("approval-resume-call", function.Name, new Dictionary<string, object?>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var output = messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString())
                .FirstOrDefault(value => value is not null);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                output is null ? "Finished after approval." : $"Finished: {output}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
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
