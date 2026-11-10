using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Lab proving native typed + sequential workflows on Microsoft.Agents.AI.Workflows 1.27.0.
/// Covers MAF-011 (typed functional executors bound via WorkflowBuilder edges) and
/// MAF-012 (native sequential multi-agent ordering). No custom workflow engine.
/// </summary>
public sealed class NativeTypedSequentialWorkflowLabTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Typed_functional_executors_bind_via_edge_and_return_typed_output()
    {
        string? observedDownstreamInput = null;
        var upper = new FunctionExecutor<string, string>(
            "lab-upper",
            (input, _, _) => ValueTask.FromResult(input.ToUpperInvariant()));
        var exclaim = new FunctionExecutor<string, string>(
            "lab-exclaim",
            (input, _, _) =>
            {
                observedDownstreamInput = input;
                return ValueTask.FromResult(input + "!");
            });

        var workflow = new WorkflowBuilder(upper)
            .AddEdge(upper, exclaim)
            .WithOutputFrom(exclaim)
            .Build();

        var result = await new NativeWorkflowRunner()
            .RunAsync(workflow, "hello")
            .WaitAsync(Timeout);

        // Typed contract: the workflow output is a string, not an untyped dictionary.
        var output = Assert.Single(result.Outputs);
        Assert.Equal("HELLO!", output.As<string>());

        // Typed handoff across the edge: the downstream executor observed the
        // upstream executor's typed string output as its typed string input.
        Assert.Equal("HELLO", observedDownstreamInput);

        // Edge traversal evidence from native events: both executors completed.
        Assert.Equal(2, result.Events.OfType<ExecutorCompletedEvent>().Count());
    }

    [Fact]
    public async Task Sequential_two_agent_workflow_preserves_step_order_and_inputs_outputs()
    {
        var invocationOrder = new List<string>();
        var invocationLock = new object();
        void RecordStep(string step)
        {
            lock (invocationLock)
            {
                invocationOrder.Add(step);
            }
        }

        var client1 = new StepRecordingChatClient("first says hi", () => RecordStep("one"));
        var client2 = new StepRecordingChatClient("second concludes", () => RecordStep("two"));
        var registry = new TwoAgentRegistry();
        AIAgent agent1 = new MafRealAgentFactory(registry, client1).CreateAgent(new AgentId("one"));
        AIAgent agent2 = new MafRealAgentFactory(registry, client2).CreateAgent(new AgentId("two"));

        var workflow = AgentWorkflowBuilder.BuildSequential([agent1, agent2]);

        var result = await new NativeWorkflowRunner()
            .RunAgentTurnAsync(workflow, [new ChatMessage(ChatRole.User, "host input")])
            .WaitAsync(Timeout);

        // Agent1 observed the host input first.
        Assert.NotEmpty(client1.Requests);
        Assert.Contains(client1.Requests[0], message =>
            message.Role == ChatRole.User && message.Text == "host input");

        // Step ordering: agent1 ran before agent2.
        int firstOne, firstTwo;
        lock (invocationLock)
        {
            firstOne = invocationOrder.IndexOf("one");
            firstTwo = invocationOrder.IndexOf("two");
        }

        Assert.True(firstOne >= 0, "Agent1 was never invoked.");
        Assert.True(firstTwo >= 0, "Agent2 was never invoked.");
        Assert.True(firstOne < firstTwo, "Agent1 must run before agent2 in a sequential workflow.");

        // Agent2 observed agent1's output in its input messages.
        Assert.Contains(client2.Requests.SelectMany(request => request), message =>
            message.Text == "first says hi");

        // Final turn output equals agent2's text.
        var updates = result.Events.OfType<AgentResponseUpdateEvent>().ToArray();
        Assert.NotEmpty(updates);
        Assert.Equal("second concludes", updates.Last().Update.Text);

        // Native events arrive in step order: agent1's text precedes agent2's text.
        var orderedUpdates = result.Events
            .Select((workflowEvent, index) => (workflowEvent, index))
            .Where(entry => entry.workflowEvent is AgentResponseUpdateEvent)
            .Select(entry => (Update: (AgentResponseUpdateEvent)entry.workflowEvent, entry.index))
            .ToArray();
        var firstAgentText = orderedUpdates.First(entry => entry.Update.Update.Text == "first says hi").index;
        var secondAgentText = orderedUpdates.First(entry => entry.Update.Update.Text == "second concludes").index;
        Assert.True(firstAgentText < secondAgentText, "Native step events must arrive in sequential order.");
    }

    private sealed class TwoAgentRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition First = new(new AgentId("one"), "One");
        private static readonly AgentDefinition Second = new(new AgentId("two"), "Two");

        public AgentDefinition? Get(AgentId id) =>
            id == First.Id ? First : id == Second.Id ? Second : null;

        public IReadOnlyList<AgentDefinition> GetAll() => [First, Second];

        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class StepRecordingChatClient : IChatClient
    {
        private readonly string _responseText;
        private readonly Action _onInvoke;

        public StepRecordingChatClient(string responseText, Action onInvoke)
        {
            _responseText = responseText;
            _onInvoke = onInvoke;
        }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public ChatClientMetadata Metadata { get; } = new("typed-sequential-lab");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.ToArray();
            lock (Requests)
            {
                Requests.Add(request);
            }

            _onInvoke();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
