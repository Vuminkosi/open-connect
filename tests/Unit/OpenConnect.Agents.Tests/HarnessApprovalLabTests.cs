// <copyright file="HarnessApprovalLabTests.cs">
// Deterministic native Harness tool-approval lab (MAF 1.27.0 + Microsoft.Extensions.AI 10.10.0.0).
// Approval prompt types: Microsoft.Extensions.AI.ToolApprovalRequestContent /
// ToolApprovalResponseContent (CreateResponse(approved, reason)); the native approval
// pipeline is Microsoft.Agents.AI.ToolApprovalAgent over ToolApprovalState
// (Rules / CollectedApprovalResponses / QueuedApprovalRequests / SurfacedApprovalRequests)
// with dont-ask-again recorded as ToolApprovalRule{ToolName, Arguments} via
// AlwaysApproveToolApprovalResponseContent (CreateAlwaysApproveToolResponse /
// CreateAlwaysApproveToolWithArgumentsResponse). HarnessAgentOptions narrows the pipeline
// via DisableToolAutoApproval / DisableApprovalNotRequiredFunctionBypassing /
// DisableApprovalResponseBinding (all default false = native behavior enabled).
// No custom approval engine; the OpenConnect override point (DelegationGate) decides
// before any native prompt is ever surfaced.
// </copyright>

using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Execution;
using Xunit;
using Xunit.Abstractions;
#pragma warning disable MEAI001 // Approval RequiresConfirmation is evaluation-only in M.E.AI 10.10.0.0; this lab pins and records the exact surface.

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves the native Harness tool-approval prompt and dont-ask-again behavior on
/// MAF 1.27.0 with a deterministic <see cref="IChatClient"/>, and records where
/// OpenConnect policy (<see cref="DelegationGate"/>) overrides/narrows native approvals.
/// </summary>
public sealed class HarnessApprovalLabTests
{
    private readonly ITestOutputHelper _output;

    public HarnessApprovalLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Approval_surface_matches_expected_native_members()
    {
        var mafMain = typeof(ToolApprovalAgent).Assembly;
        var mafAbstractions = typeof(AIAgent).Assembly;
        var harness = typeof(HarnessAgent).Assembly;
        _output.WriteLine($"MAF: {mafMain.GetName().Name} {mafMain.GetName().Version}");
        _output.WriteLine($"MAF abstractions: {mafAbstractions.GetName().Name} {mafAbstractions.GetName().Version}");
        _output.WriteLine($"Harness: {harness.GetName().Name} {harness.GetName().Version}");

        // Prompt request/response binding types live in Microsoft.Extensions.AI.Abstractions.
        var requestType = typeof(ToolApprovalRequestContent);
        var responseType = typeof(ToolApprovalResponseContent);
        Assert.Equal("Microsoft.Extensions.AI.Abstractions", requestType.Assembly.GetName().Name);
        Assert.NotNull(requestType.GetProperty("ToolCall", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(requestType.GetProperty("RequiresConfirmation", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(requestType.GetProperty("RequestId", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(requestType.GetMethod("CreateResponse", [typeof(bool), typeof(string)]));
        Assert.NotNull(responseType.GetProperty("Approved", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(responseType.GetProperty("ToolCall", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(responseType.GetProperty("Reason", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(responseType.GetProperty("RequestId", BindingFlags.Public | BindingFlags.Instance));
        _output.WriteLine("Prompt binding: ToolApprovalRequestContent{RequestId, ToolCall, RequiresConfirmation, CreateResponse(approved, reason)} -> ToolApprovalResponseContent{RequestId, Approved, ToolCall, Reason}.");

        // Native pipeline: ToolApprovalAgent + per-session ToolApprovalState (dont-ask-again store).
        var agentType = mafMain.GetType("Microsoft.Agents.AI.ToolApprovalAgent");
        Assert.NotNull(agentType);
        Assert.NotNull(agentType.GetField("DefaultMaxAutoApprovalIterations", BindingFlags.Public | BindingFlags.Static));
        var stateType = mafMain.GetType("Microsoft.Agents.AI.ToolApprovalState");
        Assert.NotNull(stateType);
        foreach (var expected in new[] { "Rules", "CollectedApprovalResponses", "QueuedApprovalRequests", "SurfacedApprovalRequests" })
        {
            Assert.NotNull(stateType.GetProperty(expected, BindingFlags.Public | BindingFlags.Instance));
        }

        var ruleType = mafMain.GetType("Microsoft.Agents.AI.ToolApprovalRule");
        Assert.NotNull(ruleType);
        Assert.NotNull(ruleType.GetProperty("ToolName", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(ruleType.GetProperty("Arguments", BindingFlags.Public | BindingFlags.Instance));
        _output.WriteLine("Dont-ask-again store: ToolApprovalState{Rules: List<ToolApprovalRule{ToolName, Arguments(IDictionary<string,string>)}>, CollectedApprovalResponses, QueuedApprovalRequests, SurfacedApprovalRequests}.");

        // Programmatic dont-ask-again signal: AlwaysApprove* wrapper content.
        var alwaysType = mafMain.GetType("Microsoft.Agents.AI.AlwaysApproveToolApprovalResponseContent");
        Assert.NotNull(alwaysType);
        Assert.NotNull(alwaysType.GetProperty("InnerResponse", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(alwaysType.GetProperty("AlwaysApproveTool", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(alwaysType.GetProperty("AlwaysApproveToolWithArguments", BindingFlags.Public | BindingFlags.Instance));
        var extensionsType = mafMain.GetType("Microsoft.Agents.AI.ToolApprovalRequestContentExtensions");
        Assert.NotNull(extensionsType);
        Assert.NotNull(extensionsType.GetMethod("CreateAlwaysApproveToolResponse", BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(extensionsType.GetMethod("CreateAlwaysApproveToolWithArgumentsResponse", BindingFlags.Public | BindingFlags.Static));
        _output.WriteLine("Dont-ask-again signal: CreateAlwaysApproveToolResponse (tool) / CreateAlwaysApproveToolWithArgumentsResponse (tool+exact args) -> recorded as ToolApprovalRule in session state.");

        // Auto-approval programmatic rules surface.
        var optionsType = mafMain.GetType("Microsoft.Agents.AI.ToolApprovalAgentOptions");
        Assert.NotNull(optionsType);
        Assert.NotNull(optionsType.GetProperty("AutoApprovalRules", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(optionsType.GetProperty("MaxAutoApprovalIterations", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(optionsType.GetProperty("JsonSerializerOptions", BindingFlags.Public | BindingFlags.Instance));
        var approvalOptions = new ToolApprovalAgentOptions();
        Assert.Null(approvalOptions.AutoApprovalRules);
        Assert.Null(approvalOptions.MaxAutoApprovalIterations);
        _output.WriteLine("ToolApprovalAgentOptions{AutoApprovalRules: IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> (default null), MaxAutoApprovalIterations (default null -> native DefaultMaxAutoApprovalIterations), JsonSerializerOptions}.");

        // Harness narrowing flags: all default false (native behavior enabled); true disables that stage.
        var harnessOptionsType = harness.GetType("Microsoft.Agents.AI.HarnessAgentOptions");
        Assert.NotNull(harnessOptionsType);
        var harnessOptions = new HarnessAgentOptions();
        Assert.False(harnessOptions.DisableToolAutoApproval);
        Assert.Null(harnessOptions.ToolApprovalAgentOptions);
        Assert.False(harnessOptions.DisableApprovalNotRequiredFunctionBypassing);
        Assert.False(harnessOptions.DisableApprovalResponseBinding);
        _output.WriteLine("HarnessAgentOptions: DisableToolAutoApproval=false (auto-approval draining enabled), ToolApprovalAgentOptions=null (native defaults), DisableApprovalNotRequiredFunctionBypassing=false (approval-not-required bypass active), DisableApprovalResponseBinding=false (inbound response binding validation active).");
        _output.WriteLine("Semantics: each Disable* flag turns OFF that native stage when set true; OpenConnect leaves all false and narrows earlier at DelegationGate (exact-args policy) instead.");
    }

    [Fact]
    public async Task Approval_granted_through_native_pipeline_executes_tool()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var executions = 0;
        var function = AIFunctionFactory.Create(() => { executions++; return "function output"; }, "sensitive_action");
        var guarded = new ApprovalRequiredAIFunction(function);
        var factory = new MafRealAgentFactory(
            new LabRegistry(new AgentDefinition(new AgentId("approval"), "Approval")), new LabApprovalClient());
        var inner = factory.CreateAgent(new AgentId("approval"), [guarded]);
        var agent = new ToolApprovalAgent(inner, new ToolApprovalAgentOptions());
        AgentSession session = await agent.CreateSessionAsync(ct);

        AgentResponse paused = await agent.RunAsync("run action", session, cancellationToken: ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        var request = Assert.IsType<ToolApprovalRequestContent>(
            Assert.Single(paused.Messages.SelectMany(message => message.Contents)));
        Assert.Equal(0, executions);
        var requestedCall = Assert.IsType<FunctionCallContent>(request.ToolCall);
        _output.WriteLine($"Native prompt surfaced via ToolApprovalAgent: tool={requestedCall.Name}, requiresConfirmation={request.RequiresConfirmation}, executions={executions}.");

        var approvalResponse = request.CreateResponse(approved: true);
        AgentResponse resumed = await agent.RunAsync(new ChatMessage(ChatRole.User, [approvalResponse]), session, cancellationToken: ct)
            .WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Equal(1, executions);
        Assert.Contains("function output", resumed.Text, StringComparison.Ordinal);
        _output.WriteLine($"Grant path: approved=true -> tool executed once; answer contains function output.");
    }

    [Fact]
    public async Task Approval_denied_through_native_pipeline_fail_closed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var executions = 0;
        var function = AIFunctionFactory.Create(() => { executions++; return "function output"; }, "sensitive_action");
        var guarded = new ApprovalRequiredAIFunction(function);
        var factory = new MafRealAgentFactory(
            new LabRegistry(new AgentDefinition(new AgentId("approval"), "Approval")), new LabApprovalClient());
        var inner = factory.CreateAgent(new AgentId("approval"), [guarded]);
        var agent = new ToolApprovalAgent(inner, new ToolApprovalAgentOptions());
        AgentSession session = await agent.CreateSessionAsync(ct);

        AgentResponse paused = await agent.RunAsync("run action", session, cancellationToken: ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        var request = Assert.IsType<ToolApprovalRequestContent>(
            Assert.Single(paused.Messages.SelectMany(message => message.Contents)));

        AgentResponse declined = await agent.RunAsync(
            new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false)]), session, cancellationToken: ct)
            .WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Equal(0, executions);
        Assert.NotNull(declined);
        Assert.DoesNotContain("Finished: function output", declined.Text ?? string.Empty, StringComparison.Ordinal);
        _output.WriteLine("Deny path: approved=false -> tool did NOT execute (fail closed); output absent from answer.");
    }

    [Fact]
    public async Task Dont_ask_again_grant_persists_for_identical_calls_via_native_state()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;
        var executions = 0;
        var function = AIFunctionFactory.Create(() => { executions++; return "function output"; }, "sensitive_action");
        var guarded = new ApprovalRequiredAIFunction(function);
        var client = new LabApprovalClient();
        var factory = new MafRealAgentFactory(
            new LabRegistry(new AgentDefinition(new AgentId("approval"), "Approval")), client);
        var inner = factory.CreateAgent(new AgentId("approval"), [guarded]);
        var agent = new ToolApprovalAgent(inner, new ToolApprovalAgentOptions());
        AgentSession session = await agent.CreateSessionAsync(ct);

        AgentResponse first = await agent.RunAsync("run action", session, cancellationToken: ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        var request = Assert.IsType<ToolApprovalRequestContent>(
            Assert.Single(first.Messages.SelectMany(message => message.Contents)));
        Assert.Equal(0, executions);

        // Programmatic dont-ask-again for this exact tool+args through the native pipeline.
        AlwaysApproveToolApprovalResponseContent alwaysApprove =
            request.CreateAlwaysApproveToolWithArgumentsResponse("lab: same args approved");
        Assert.True(alwaysApprove.AlwaysApproveToolWithArguments);
        AgentResponse firstResumed = await agent.RunAsync(new ChatMessage(ChatRole.User, [alwaysApprove]), session, cancellationToken: ct)
            .WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(1, executions);
        Assert.Contains("function output", firstResumed.Text, StringComparison.Ordinal);
        _output.WriteLine("Turn 1: always-approve-with-args recorded in native ToolApprovalState.Rules; tool executed.");

        // Identical second call: no new prompt should surface; the tool runs via the stored rule.
        AgentResponse second = await agent.RunAsync("run action", session, cancellationToken: ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(2, executions);
        Assert.DoesNotContain(second.Messages.SelectMany(message => message.Contents),
            content => content is ToolApprovalRequestContent);
        Assert.Contains("function output", second.Text, StringComparison.Ordinal);
        _output.WriteLine($"Turn 2 (identical args): no new ToolApprovalRequestContent surfaced; native rule auto-approved (executions={executions}).");
    }

    /// <summary>
    /// Gate denial proven in isolation: <see cref="DelegationGate"/> fails closed here.
    /// The native approval pipeline (grant/deny/persist) is proven separately by the
    /// three ToolApprovalAgent tests above; an integrated gated-pipeline proof
    /// (gate + native agent in one run) is future work.
    /// </summary>
    [Fact]
    public void OpenConnect_policy_denies_delegation_before_any_native_prompt_is_built()
    {
        // Isolated gate check only: no ToolApprovalAgent/HarnessAgent is built and no
        // native prompt is surfaced in this test. Native pipeline behavior is covered
        // separately above; integrated gated-pipeline proof is future work (resumption).
        // (No tool scaffolding here by intent: nothing executes before the gate denies.)

        var parent = new AgentId("parent");
        var child = new AgentId("child");
        var catalog = new AgentCatalog();
        catalog.Register(LabDefinition(child, [new ToolId("tool")], ["read"]));
        catalog.Register(LabDefinition(parent, [new ToolId("tool")], ["read"], children: []));
        var gate = new DelegationGate(catalog);
        var parentContext = new DelegationParentContext(parent, Guid.NewGuid(), 0, [],
            new HashSet<ToolId> { new("tool") }, new HashSet<string> { "read" },
            new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
            AgentExecutionMode.Interactive, 0, 0, 0);

        // Isolated denial: parent is not authorized to delegate to this child, so the
        // gate fails closed here. No native agent is created and no
        // ToolApprovalRequestContent is surfaced in this test.
        var denied = Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(
            parentContext, child,
            new DelegationRequest(new HashSet<ToolId> { new("tool") }, new HashSet<string> { "read" })));
        _output.WriteLine($"Isolated gate denial (no native pipeline built in this test): DelegationGate.AuthorizeDelegation denied ({denied.Message}); integrated gated-pipeline proof is future work.");
        _output.WriteLine("Recorded narrowing: DelegationGate enforces exact tool+scope intersection and budget BEFORE Harness ToolApprovalAgent; native approval remains the second (human/operator) layer, never a substitute for host authorization.");
    }

    private static AgentDefinition LabDefinition(AgentId id, ToolId[] tools, string[] scopes, AgentId[]? children = null) =>
        new(id, id.Value)
        {
            ExecutionMode = AgentExecutionMode.Interactive,
            IdentityMode = AgentIdentityMode.ActiveUser,
            Operational = null,
            Orchestration = new(MaxChildren: 0, AllowedChildAgents: children ?? []),
            ToolPolicy = new(tools, scopes),
            Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
        };

    private sealed class LabApprovalClient : IChatClient
    {
        private int _calls;
        public ChatClientMetadata Metadata { get; } = new("harness-approval-lab");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Trailing-edge decision only: turn history carries stale approvals/results,
            // so a fresh prompt is recognized by its trailing message, not by scanning history.
            var snapshot = messages.ToArray();
            var last = snapshot[^1];
            var trailingResult = last.Contents.OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString())
                .FirstOrDefault(value => value is not null);
            if (trailingResult is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Finished: {trailingResult}")));
            }

            if (last.Contents.Any(content =>
                content is ToolApprovalResponseContent || content is AlwaysApproveToolApprovalResponseContent))
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finished after approval.")));
            }

            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
            var callId = $"lab-approval-call-{Interlocked.Increment(ref _calls)}";
            AIContent call = new FunctionCallContent(callId, function.Name, new Dictionary<string, object?>());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LabRegistry(AgentDefinition definition) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => id == definition.Id ? definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }
}
