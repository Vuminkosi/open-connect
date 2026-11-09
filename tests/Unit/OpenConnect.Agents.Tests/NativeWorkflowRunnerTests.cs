using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class NativeWorkflowRunnerTests
{
    [Fact]
    public async Task Runs_host_built_native_workflow_and_returns_native_outputs()
    {
        var client = new DeterministicChatClient("expected native answer");
        var agent = CreateAgent(client);
        var workflow = AgentWorkflowBuilder.BuildSequential([agent]);

        var result = await new NativeWorkflowRunner().RunAgentTurnAsync(
            workflow, new List<ChatMessage> { new(ChatRole.User, "host input") });

        Assert.Contains(client.Requests, request => request.Any(message =>
            message.Role == ChatRole.User && message.Text == "host input"));
        Assert.NotEmpty(result.Events);
        Assert.Contains(result.Events.OfType<AgentResponseUpdateEvent>(), updateEvent =>
            updateEvent.Update.Text == "expected native answer");
    }

    [Fact]
    public async Task Native_workflow_error_event_propagates_failure_instead_of_returning_success()
    {
        var failure = new InvalidOperationException("deterministic native failure");
        var client = new DeterministicChatClient("unused") { Failure = failure };
        var workflow = AgentWorkflowBuilder.BuildSequential([CreateAgent(client)]);

        var observed = await Assert.ThrowsAsync<TargetInvocationException>(() => new NativeWorkflowRunner()
            .RunAgentTurnAsync(workflow, new List<ChatMessage> { new(ChatRole.User, "host input") }));

        var cause = Assert.IsType<InvalidOperationException>(observed.InnerException);
        Assert.Equal(failure.Message, cause.Message);
    }

    [Fact]
    public async Task Pre_cancelled_run_does_not_report_completion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workflow = AgentWorkflowBuilder.BuildSequential([CreateAgent(new DeterministicChatClient())]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeWorkflowRunner()
            .RunAgentTurnAsync(workflow, new List<ChatMessage> { new(ChatRole.User, "host input") }, cancellation.Token));
    }

    [Fact]
    public async Task Mid_run_cancellation_cancels_native_execution_and_does_not_return_success()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new BlockingChatClient();
        var workflow = AgentWorkflowBuilder.BuildSequential([CreateAgent(client)]);
        var execution = new NativeWorkflowRunner().RunAgentTurnAsync(
            workflow, new List<ChatMessage> { new(ChatRole.User, "host input") }, cancellation.Token);

        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static AIAgent CreateAgent(IChatClient client) =>
        new MafRealAgentFactory(new TestRegistry(), client).CreateAgent(new AgentId("workflow"));

    private sealed class TestRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("workflow"), "Workflow");

        public AgentDefinition? Get(AgentId id) => id == Definition.Id ? Definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class DeterministicChatClient : IChatClient
    {
        private readonly string _responseText;

        public DeterministicChatClient(string responseText = "native workflow response") => _responseText = responseText;

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("native-workflow-test");
        public Exception? Failure { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = CreateResponse(messages);
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var response = CreateResponse(messages);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        private ChatResponse CreateResponse(IEnumerable<ChatMessage> messages)
        {
            var request = messages.ToArray();
            Requests.Add(request);
            if (Failure is not null)
            {
                throw Failure;
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseText));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChatClientMetadata Metadata { get; } = new("native-workflow-cancellation-test");

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
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
