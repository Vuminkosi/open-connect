using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Lab proving the NATIVE handoff primitive on Microsoft.Agents.AI.Workflows 1.27.0
/// (covers MAF-014). Uses <see cref="HandoffWorkflowBuilder"/> (handoff functions named
/// <c>handoff_to_*</c>) with deterministic stub chat clients (no network): a triage
/// participant hands off to a specialist participant, the specialist turn carries the
/// triage turn, each participant runs under its own owned session, and request-port
/// semantics are recorded. No custom handoff engine; the policy
/// <c>OrchestrationAdapters.HandoffRunner</c> adapter is not exercised here.
/// </summary>
public sealed class NativeHandoffWorkflowLabTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ITestOutputHelper _output;

    public NativeHandoffWorkflowLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Native_handoff_routes_triage_to_specialist_with_continuity_and_owned_sessions()
    {
        const string hostInput = "host input";
        const string triageMarker = "triage-note:MARKER-7";
        const string specialistFinal = "specialist-final";

        var triageClient = new HandoffStubClient(triageMarker, emitHandoffOnFirstCall: true);
        var specialistClient = new HandoffStubClient(specialistFinal, emitHandoffOnFirstCall: false);
        var registry = new TwoAgentRegistry();
        AIAgent triage = new MafRealAgentFactory(registry, triageClient).CreateAgent(new AgentId("triage"));
        AIAgent specialist = new MafRealAgentFactory(registry, specialistClient).CreateAgent(new AgentId("specialist"));

        // Native handoff primitive (1.27.0): HandoffWorkflowBuilder wires handoff_to_*
        // functions; the triage stub emits the registered handoff call deterministically.
        var workflow = new HandoffWorkflowBuilder(triage)
            .WithHandoffs(triage, [specialist])
            .Build();

        var result = await new NativeWorkflowRunner()
            .RunAgentTurnAsync(workflow, [new ChatMessage(ChatRole.User, hostInput)])
            .WaitAsync(Timeout);

        // Each participant ran exactly once under its own session.
        Assert.Single(triageClient.Requests);
        Assert.Single(specialistClient.Requests);

        // The native handoff function was offered to triage; record its exact shape.
        var handoffFunction = Assert.Single(
            triageClient.FirstCallToolNames,
            name => name.StartsWith("handoff_to_", StringComparison.Ordinal));
        _output.WriteLine($"Native handoff function: {handoffFunction} (prefix handoff_to_ + native target key).");

        // Participant session ownership: distinct native executors, each bound to its agent.
        var completed = result.Events.OfType<ExecutorCompletedEvent>().ToArray();
        var triagePrefix = (triage.Name ?? "triage").Replace("-", "_", StringComparison.Ordinal) + "_";
        var specialistPrefix = (specialist.Name ?? "specialist").Replace("-", "_", StringComparison.Ordinal) + "_";
        var triageExecutor = Assert.Single(completed, e =>
            e.ExecutorId.StartsWith(triagePrefix, StringComparison.Ordinal));
        var specialistExecutor = Assert.Single(completed, e =>
            e.ExecutorId.StartsWith(specialistPrefix, StringComparison.Ordinal));
        Assert.NotEqual(triageExecutor.ExecutorId, specialistExecutor.ExecutorId);
        Assert.EndsWith(triage.Id.Replace("-", string.Empty, StringComparison.Ordinal), triageExecutor.ExecutorId, StringComparison.Ordinal);
        Assert.EndsWith(specialist.Id.Replace("-", string.Empty, StringComparison.Ordinal), specialistExecutor.ExecutorId, StringComparison.Ordinal);
        _output.WriteLine($"Owned sessions: {triageExecutor.ExecutorId} -> {specialistExecutor.ExecutorId}.");

        // Isolation: triage never observed the specialist turn; the specialist turn
        // carries the triage turn (multi-turn continuity through the native handoff).
        Assert.DoesNotContain(triageClient.Requests[0], message => message.Text == specialistFinal);
        Assert.Contains(specialistClient.Requests[0], message =>
            message.Role == ChatRole.User && message.Text == hostInput);
        Assert.Contains(specialistClient.Requests[0], message => message.Text == triageMarker);
        _output.WriteLine(
            $"Continuity: specialist observed [{string.Join("|", specialistClient.Requests[0].Select(message => message.Role + ":" + message.Text))}].");

        // Native event order: triage completed before the specialist; the final native
        // agent update is the specialist answer.
        Assert.True(
            Array.IndexOf(completed, triageExecutor) < Array.IndexOf(completed, specialistExecutor),
            "Triage must complete before the specialist in a native handoff.");
        var updates = result.Events.OfType<AgentResponseUpdateEvent>().ToArray();
        Assert.NotEmpty(updates);
        Assert.Equal(specialistFinal, updates.Last().Update.Text);

        var output = Assert.Single(result.Outputs, o => o.Data is List<ChatMessage>);
        var outputMessages = Assert.IsType<List<ChatMessage>>(output.As<List<ChatMessage>>());
        Assert.Contains(outputMessages, message => message.Text == specialistFinal);
    }

    [Fact]
    public async Task Interactive_request_port_turn_carries_prior_turn_payload()
    {
        // Native request-port primitive (1.27.0): AddExternalCall halts the run with an
        // ExternalRequest carrying port id + typed payload; the host response resumes it.
        var gate = new FunctionExecutor<string, object>(
            "lab-handoff-port-gate",
            (input, _, _) => ValueTask.FromResult<object>(
                input is "start" or "approved" ? (object)new PortPayload(input) : (object)("continued:" + input)));
        var workflow = new WorkflowBuilder(gate)
            .AddExternalCall<PortPayload, string>(gate, "approval")
            .WithOutputFrom(gate)
            .Build();

        await using var interaction = await NativeWorkflowInteraction
            .StartAsync(workflow, "start")
            .WaitAsync(Timeout);

        var first = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.Equal("approval", first.PortInfo.PortId);
        Assert.True(first.IsDataOfType<PortPayload>());
        Assert.True(first.TryGetDataAs<PortPayload>(out var firstPayload));
        Assert.Equal("start", firstPayload!.Text);

        await interaction.RespondAsync(first, "approved", (_, _) => ValueTask.FromResult(true))
            .WaitAsync(Timeout);

        // Turn 2 carries turn 1: the second halt payload is the first turn's response.
        var second = Assert.IsType<ExternalRequest>(interaction.PendingExternalRequest);
        Assert.Equal("approval", second.PortInfo.PortId);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.True(second.TryGetDataAs<PortPayload>(out var secondPayload));
        Assert.Equal("approved", secondPayload!.Text);

        // Request-port response linkage: the native response echoes port + request id.
        var linked = second.CreateResponse("final");
        Assert.Equal(second.RequestId, linked.RequestId);
        Assert.Equal(second.PortInfo.PortId, linked.PortInfo.PortId);
        _output.WriteLine(
            $"Request-port semantics: port=approval request={typeof(PortPayload).FullName} " +
            $"response={typeof(string).FullName} first={first.RequestId} second={second.RequestId}.");

        await interaction.RespondAsync(second, "final", (_, _) => ValueTask.FromResult(true))
            .WaitAsync(Timeout);
        Assert.Null(interaction.PendingExternalRequest);
    }

    private sealed class TwoAgentRegistry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Triage = new(new AgentId("triage"), "Triage");
        private static readonly AgentDefinition Specialist = new(new AgentId("specialist"), "Specialist");

        public AgentDefinition? Get(AgentId id) =>
            id == Triage.Id ? Triage : id == Specialist.Id ? Specialist : null;

        public IReadOnlyList<AgentDefinition> GetAll() => [Triage, Specialist];

        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class HandoffStubClient : IChatClient
    {
        private readonly string _payloadText;
        private readonly bool _emitHandoff;

        public HandoffStubClient(string payloadText, bool emitHandoffOnFirstCall)
        {
            _payloadText = payloadText;
            _emitHandoff = emitHandoffOnFirstCall;
        }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<string> FirstCallToolNames { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("native-handoff-lab");

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

            if (_emitHandoff && Requests.Count == 1)
            {
                var toolNames = options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
                lock (FirstCallToolNames)
                {
                    FirstCallToolNames.AddRange(toolNames);
                }

                var handoffFunction = toolNames.FirstOrDefault(name =>
                    name.StartsWith("handoff_to_", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("Native handoff function was not offered to triage.");
                var contents = new List<AIContent>
                {
                    new TextContent(_payloadText),
                    new FunctionCallContent("lab-handoff-1", handoffFunction,
                        new Dictionary<string, object?> { ["reasonForHandoff"] = "needs specialist" }),
                };
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _payloadText)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                foreach (var content in message.Contents)
                {
                    yield return new ChatResponseUpdate(message.Role, new List<AIContent> { content });
                }
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed record PortPayload(string Text);
}
