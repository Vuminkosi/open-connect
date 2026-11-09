// <copyright file="HarnessBackgroundDelegationLabTests.cs">
// Deterministic native Harness background delegation lab (MAF 1.27.0 + Microsoft.Extensions.AI 10.10.0.0).
// Native surface under test:
// - HarnessAgentOptions.BackgroundAgents (IEnumerable{AIAgent}) +
//   HarnessAgentOptions.BackgroundAgentsProviderOptions (BackgroundAgentsProviderOptions) are the
//   native Harness opt-in for background delegation.
// - Microsoft.Agents.AI.BackgroundAgentsProvider (public, [Experimental("MAAI001")]) is the native
//   AIContextProvider. Public operation surface: ctor(IEnumerable{AIAgent}, BackgroundAgentsProviderOptions),
//   GetIncompleteTasks(AgentSession), ReleaseSessionAsync(session, cancelRunning, timeout, ct),
//   StateKeys. Internal only (not drivable without private reflection): BackgroundAgentState,
//   BackgroundAgentRuntimeState, CreateTools/StartTrackedRun/TryRefreshTaskState.
// - 6 native tools (exact names, schemas pinned by probe on 1.27.0):
//   background_agents_start_task(agentName, input, description) -> "Background task {id} started on agent '{name}'.";
//   background_agents_wait_for_first_completion(taskIds[]) -> completion/timeout status text;
//   background_agents_get_task_results(taskId) -> result text when complete;
//   background_agents_get_all_tasks() / background_agents_continue_task(taskId, text) /
//   background_agents_clear_completed_task(taskId).
// - BackgroundTaskInfo{Id, AgentName, Description, Status, ResultText, ErrorText} with
//   BackgroundTaskStatus{Running, Completed, Failed, Lost} is the native completion record.
// - BackgroundAgentsProviderOptions{Instructions, AgentListBuilder, WaitTimeout} (default WaitTimeout 00:05:00).
// OpenConnect override points (no custom delegation engine): DelegationGate authorizes scope/depth/budget
// before any native background run is spawned; RunBudgetLedger enforces the granted slice at run time.
// This lab drives the REAL provider in the REAL Harness pipeline (no Task.Run wrappers presented as native).
// </copyright>

#pragma warning disable MAAI001 // Background delegation API is evaluation-only in MAF 1.27.0; this lab pins the exact surface.

using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Execution;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves native Harness background delegation on MAF 1.27.0 with a deterministic
/// <see cref="IChatClient"/>, and records where OpenConnect policy
/// (<see cref="DelegationGate"/> / <see cref="RunBudgetLedger"/>) constrains the
/// background child before/within the native run (depth ceiling 2, scope intersection,
/// parent budget slice).
/// </summary>
public sealed class HarnessBackgroundDelegationLabTests
{
    private const string StartTool = "background_agents_start_task";
    private const string WaitTool = "background_agents_wait_for_first_completion";
    private const string ResultsTool = "background_agents_get_task_results";

    private readonly ITestOutputHelper _output;

    public HarnessBackgroundDelegationLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Background_surface_matches_expected_native_members()
    {
        var main = typeof(ChatClientAgent).Assembly;
        Assembly harness;
        try
        {
            harness = Assembly.Load("Microsoft.Agents.AI.Harness");
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new MafCapabilityUnavailableException("Harness", "Microsoft.Agents.AI.HarnessAgent");
        }

        _output.WriteLine($"MAF: {main.GetName().Name} {main.GetName().Version}");
        _output.WriteLine($"Harness: {harness.GetName().Name} {harness.GetName().Version}");

        // Harness opt-in surface.
        var harnessOptionsType = harness.GetType("Microsoft.Agents.AI.HarnessAgentOptions");
        Assert.NotNull(harnessOptionsType);
        Assert.NotNull(harnessOptionsType.GetProperty("BackgroundAgents", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(harnessOptionsType.GetProperty("BackgroundAgentsProviderOptions", BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(typeof(IEnumerable<AIAgent>), harnessOptionsType.GetProperty("BackgroundAgents")!.PropertyType);
        _output.WriteLine("HarnessAgentOptions.BackgroundAgents: IEnumerable<AIAgent>; BackgroundAgentsProviderOptions: BackgroundAgentsProviderOptions.");

        // Native provider surface.
        var providerType = main.GetType("Microsoft.Agents.AI.BackgroundAgentsProvider");
        Assert.NotNull(providerType);
        Assert.True(providerType.IsPublic);
        Assert.Contains(providerType.GetCustomAttributesData(),
            attr => attr.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.ExperimentalAttribute");
        var ctor = providerType.GetConstructor([typeof(IEnumerable<AIAgent>), main.GetType("Microsoft.Agents.AI.BackgroundAgentsProviderOptions")!]);
        Assert.NotNull(ctor);
        Assert.NotNull(providerType.GetMethod("GetIncompleteTasks", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(providerType.GetMethod("ReleaseSessionAsync", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(providerType.GetProperty("StateKeys", BindingFlags.Public | BindingFlags.Instance));
        _output.WriteLine("BackgroundAgentsProvider: public, [Experimental(MAAI001)], ctor(IEnumerable<AIAgent>, BackgroundAgentsProviderOptions), GetIncompleteTasks/ReleaseSessionAsync/StateKeys public; state/runtime/CreateTools internal.");

        // Native completion record.
        var infoType = main.GetType("Microsoft.Agents.AI.BackgroundTaskInfo");
        Assert.NotNull(infoType);
        foreach (var expected in new[] { "Id", "AgentName", "Description", "Status", "ResultText", "ErrorText" })
        {
            Assert.NotNull(infoType.GetProperty(expected, BindingFlags.Public | BindingFlags.Instance));
        }

        var statusType = main.GetType("Microsoft.Agents.AI.BackgroundTaskStatus");
        Assert.NotNull(statusType);
        Assert.True(statusType.IsEnum);
        foreach (var expected in new[] { "Running", "Completed", "Failed", "Lost" })
        {
            Assert.True(Enum.IsDefined(statusType, expected));
        }

        _output.WriteLine("BackgroundTaskInfo{Id, AgentName, Description, Status, ResultText, ErrorText}; BackgroundTaskStatus{Running, Completed, Failed, Lost}.");

        // Native provider options.
        var providerOptions = new BackgroundAgentsProviderOptions();
        Assert.Equal(TimeSpan.FromMinutes(5), providerOptions.WaitTimeout);
        _output.WriteLine($"BackgroundAgentsProviderOptions{{Instructions, AgentListBuilder, WaitTimeout}} default WaitTimeout={providerOptions.WaitTimeout}.");
    }

    [Fact]
    public async Task Native_background_delegate_runs_to_completion_with_real_tool_execution()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;

        var childExecutions = 0;
        var function = AIFunctionFactory.Create(() => { childExecutions++; return "background payload"; }, "bg_lookup");
        var childClient = new LabBackgroundChildClient();
        var childId = new AgentId("bg-worker");
        var factory = new MafRealAgentFactory(
            new LabRegistry(new AgentDefinition(childId, "bg-worker")), childClient);
        AIAgent childAgent = factory.CreateAgent(childId, [function]);
        Assert.Equal("bg-worker", childAgent.Name);

#pragma warning disable MAAI001 // Intentional native provider use; see file header.
        var provider = new BackgroundAgentsProvider([childAgent], new BackgroundAgentsProviderOptions());
#pragma warning restore MAAI001

        var options = new HarnessAgentOptions
        {
            Id = "lab-bg-harness",
            Name = "LabBackgroundHarness",
            Description = "Deterministic background delegation lab.",
            AIContextProviders = [provider],
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = true,
        };

        var hostClient = new LabBackgroundHostClient();
        HarnessAgent agent = hostClient.AsHarnessAgent(options);
        AgentSession session = await agent.CreateSessionAsync(ct);

        AgentResponse response = await agent.RunAsync("lab: delegate the background lookup", session, cancellationToken: ct)
            .WaitAsync(TimeSpan.FromSeconds(60), ct);

        Assert.NotNull(response);
        // Real native completion: the child tool executed exactly once inside the background run
        // (two child model turns: emit the call, then finish on the tool result).
        Assert.Equal(1, childExecutions);
        Assert.Equal(2, childClient.Calls);
        // Real native completion state (not a test-set flag): no incomplete tasks remain.
        Assert.Empty(provider.GetIncompleteTasks(session));
        // Native result propagation: the child's real output flowed back into the host context.
        Assert.Contains(hostClient.SeenResults, text => text.Contains("background payload", StringComparison.Ordinal));
        // Native tools flowed through the pipeline in spawn -> wait -> results order.
        Assert.Contains(hostClient.ToolsPerCall, entry => entry.Split(',').Contains(StartTool, StringComparer.Ordinal));
        Assert.Contains(hostClient.ToolsPerCall, entry => entry.Split(',').Contains(WaitTool, StringComparer.Ordinal));
        Assert.Contains(hostClient.ToolsPerCall, entry => entry.Split(',').Contains(ResultsTool, StringComparer.Ordinal));
        _output.WriteLine($"Native background run complete: childExecutions={childExecutions}, incomplete=0, hostCalls={hostClient.Calls}, tools=[{string.Join(" | ", hostClient.ToolsPerCall)}].");
        _output.WriteLine($"Spawn confirmation: '{hostClient.SpawnConfirmation}'.");
    }

    [Fact]
    public void OpenConnect_policy_constrains_background_child_scopes_to_parent_grant()
    {
        var parent = new AgentId("bg-parent");
        var child = new AgentId("bg-worker");
        var tool = new ToolId("tool");
        var extra = new ToolId("extra");
        var catalog = new AgentCatalog();
        catalog.Register(new AgentDefinition(child, "BgWorker")
        {
            ExecutionMode = AgentExecutionMode.Interactive,
            IdentityMode = AgentIdentityMode.ActiveUser,
            Operational = null,
            Orchestration = new(MaxChildren: 0, AllowedChildAgents: []),
            ToolPolicy = new([tool, extra], ["read"]),
            Budget = new AgentBudget(10, 10, 1000, TimeSpan.FromMinutes(10), 4, 4, 8),
        });
        catalog.Register(new AgentDefinition(parent, "BgParent")
        {
            ExecutionMode = AgentExecutionMode.Interactive,
            IdentityMode = AgentIdentityMode.ActiveUser,
            Operational = null,
            Orchestration = new(MaxChildren: 2, AllowedChildAgents: [child]),
            ToolPolicy = new([tool, extra], ["read", "write"]),
            Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
        });

        var gate = new DelegationGate(catalog);
        var parentCtx = new DelegationParentContext(parent, Guid.NewGuid(), 0, [],
            new HashSet<ToolId> { tool, extra }, new HashSet<string> { "read", "write" },
            new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
            AgentExecutionMode.Interactive, 0, 0, 0);

        // Intersection: child keeps only what parent AND child both authorize.
        var grant = gate.AuthorizeDelegation(parentCtx, child,
            new DelegationRequest(new HashSet<ToolId> { tool }, new HashSet<string> { "read" }));
        Assert.Equal(1, grant.ChildDepth);
        Assert.Equal([tool], grant.EffectiveToolIds);
        Assert.Equal(["read"], grant.EffectiveScopes);
        Assert.True(grant.EffectiveScopes.All(scope => parentCtx.EffectiveScopes.Contains(scope)));
        Assert.True(grant.EffectiveToolIds.All(toolId => parentCtx.EffectiveToolIds.Contains(toolId)));

        // Anything the child definition lacks is denied before any native run is spawned.
        Assert.Throws<AgentCatalogException>(() => gate.AuthorizeDelegation(parentCtx, child,
            new DelegationRequest(new HashSet<ToolId> { tool }, new HashSet<string> { "write" })));
        _output.WriteLine($"Scope intersection: grant scopes=[{string.Join(",", grant.EffectiveScopes)}] âŠ† parent; 'write' request denied (child lacks it).");
    }

    [Fact]
    public void Depth3_background_attempt_denied_before_any_child_model_or_tool_begins()
    {
        var parent = new AgentId("bg-parent");
        var child = new AgentId("bg-worker");
        var tool = new ToolId("tool");
        var catalog = new AgentCatalog();
        catalog.Register(new AgentDefinition(child, "BgWorker")
        {
            ExecutionMode = AgentExecutionMode.Interactive,
            IdentityMode = AgentIdentityMode.ActiveUser,
            Operational = null,
            Orchestration = new(MaxChildren: 0, AllowedChildAgents: []),
            ToolPolicy = new([tool], ["read"]),
            Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
        });
        catalog.Register(new AgentDefinition(parent, "BgParent")
        {
            ExecutionMode = AgentExecutionMode.Interactive,
            IdentityMode = AgentIdentityMode.ActiveUser,
            Operational = null,
            Orchestration = new(MaxChildren: 2, AllowedChildAgents: [child]),
            ToolPolicy = new([tool], ["read"]),
            Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
        });

        // The native child agent object exists but is never run: the gate denies first.
        var childClient = new LabBackgroundChildClient();
        var factory = new MafRealAgentFactory(
            new LabRegistry(new AgentDefinition(child, "bg-worker")), childClient);
        AIAgent childAgent = factory.CreateAgent(child, []);
        Assert.Equal("bg-worker", childAgent.Name);

        var gate = new DelegationGate(catalog);
        var parentCtx = new DelegationParentContext(parent, Guid.NewGuid(), 2, [parent],
            new HashSet<ToolId> { tool }, new HashSet<string> { "read" },
            new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
            AgentExecutionMode.Interactive, 0, 0, 0);

        var denied = Assert.Throws<DelegationDepthExceededException>(() => gate.AuthorizeDelegation(parentCtx, child,
            new DelegationRequest(new HashSet<ToolId> { tool }, new HashSet<string> { "read" })));
        Assert.Equal(3, denied.RequestedDepth);
        Assert.Equal(AgentCatalog.FrameworkMaxDepth, denied.MaximumDepth);
        Assert.Equal(2, AgentCatalog.FrameworkMaxDepth);
        Assert.Equal(0, childClient.Calls);
        _output.WriteLine($"Depth ceiling holds: {denied.Message}; child model calls={childClient.Calls} (nothing began).");
    }

    [Fact]
    public void Background_child_cannot_exceed_parent_budget_slice()
    {
        var parent = new AgentId("bg-parent");
        var child = new AgentId("bg-worker");
        var tool = new ToolId("tool");
        var catalog = new AgentCatalog();
        catalog.Register(new AgentDefinition(child, "BgWorker")
        {
            ExecutionMode = AgentExecutionMode.Interactive,
            IdentityMode = AgentIdentityMode.ActiveUser,
            Operational = null,
            Orchestration = new(MaxChildren: 0, AllowedChildAgents: []),
            ToolPolicy = new([tool], ["read"]),
            Budget = new AgentBudget(10, 10, 1000, TimeSpan.FromMinutes(10), 4, 4, 8),
        });
        catalog.Register(new AgentDefinition(parent, "BgParent")
        {
            ExecutionMode = AgentExecutionMode.Interactive,
            IdentityMode = AgentIdentityMode.ActiveUser,
            Operational = null,
            Orchestration = new(MaxChildren: 2, AllowedChildAgents: [child]),
            ToolPolicy = new([tool], ["read"]),
            Budget = new AgentBudget(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4),
        });

        var gate = new DelegationGate(catalog);
        var parentRemaining = new AgentBudget(3, 2, 100, TimeSpan.FromMinutes(1), 2, 2, 2);
        var parentCtx = new DelegationParentContext(parent, Guid.NewGuid(), 0, [],
            new HashSet<ToolId> { tool }, new HashSet<string> { "read" },
            parentRemaining, AgentExecutionMode.Interactive, 0, 0, 0);
        var grant = gate.AuthorizeDelegation(parentCtx, child,
            new DelegationRequest(new HashSet<ToolId> { tool }, new HashSet<string> { "read" }));

        // NarrowBudget: every residual bound is the min of parent remainder and child definition.
        Assert.True(grant.RemainingBudget.MaxToolCalls <= parentRemaining.MaxToolCalls);
        Assert.True(grant.RemainingBudget.MaxModelCalls <= parentRemaining.MaxModelCalls);
        Assert.True(grant.RemainingBudget.MaxTokens <= parentRemaining.MaxTokens);
        Assert.Equal(3, grant.RemainingBudget.MaxToolCalls);
        Assert.Equal(2, grant.RemainingBudget.MaxModelCalls);
        Assert.Equal(100, grant.RemainingBudget.MaxTokens);

        // RunBudgetLedger enforces the granted slice: tool calls exhaust, subagent slot exhausts.
        var ledger = new RunBudgetLedger(grant.RemainingBudget);
        Assert.True(ledger.TryReserveToolCall());
        Assert.True(ledger.TryReserveToolCall());
        Assert.True(ledger.TryReserveToolCall());
        Assert.False(ledger.TryReserveToolCall());
        Assert.True(ledger.TryReserveSubagentWithSlot());
        Assert.False(ledger.TryReserveSubagentWithSlot());
        Assert.Equal(0, ledger.Remaining.MaxToolCalls);
        _output.WriteLine($"Budget slice: grant=({grant.RemainingBudget.MaxToolCalls}t,{grant.RemainingBudget.MaxModelCalls}m,{grant.RemainingBudget.MaxTokens}tok); 4th tool call denied, 2nd subagent denied.");
    }

    private sealed class LabRegistry(AgentDefinition definition) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => id == definition.Id ? definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    /// <summary>Deterministic background child: runs its one real tool once, then finishes.</summary>
    private sealed class LabBackgroundChildClient : IChatClient
    {
        public int Calls;
        public ChatClientMetadata Metadata { get; } = new("bg-child-lab");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            var snapshot = messages.ToArray();
            var last = snapshot[^1];
            var trailingResult = last.Contents.OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString())
                .FirstOrDefault(value => value is not null);
            if (trailingResult is not null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"Finished: {trailingResult}")));
            }

            var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(options!.Tools!));
            AIContent call = new FunctionCallContent($"lab-bg-child-{Calls}", function.Name, new Dictionary<string, object?>());
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

    /// <summary>
    /// Deterministic host: scripts spawn -&gt; wait -&gt; results from conversation evidence
    /// (never a fixed call counter), then returns final text.
    /// </summary>
    private sealed class LabBackgroundHostClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("bg-host-lab");
        public List<string> ToolsPerCall { get; } = [];
        public List<string> SeenResults { get; } = [];
        public string SpawnConfirmation { get; private set; } = string.Empty;
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            var tools = options?.Tools?.OfType<AIFunction>().ToArray() ?? [];
            ToolsPerCall.Add(string.Join(",", tools.Select(tool => tool.Name)));
            Interlocked.Increment(ref Calls);

            var calls = snapshot.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
            var results = snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            foreach (var result in results)
            {
                var text = result.Result?.ToString();
                if (text is not null && !SeenResults.Contains(text, StringComparer.Ordinal))
                {
                    SeenResults.Add(text);
                }
            }

            AIFunction RequireTool(string name)
            {
                var function = tools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
                return function ?? throw new InvalidOperationException(
                    $"Lab host expected native tool '{name}' but the pipeline supplied [{ToolsPerCall[^1]}].");
            }

            var startCall = calls.FirstOrDefault(call => string.Equals(call.Name, StartTool, StringComparison.Ordinal));
            var waitCall = calls.FirstOrDefault(call => string.Equals(call.Name, WaitTool, StringComparison.Ordinal));
            var resultsCall = calls.FirstOrDefault(call => string.Equals(call.Name, ResultsTool, StringComparison.Ordinal));

            if (startCall is null)
            {
                var function = RequireTool(StartTool);
                AIContent call = new FunctionCallContent("lab-bg-start-1", StartTool, new Dictionary<string, object?>
                {
                    ["agentName"] = "bg-worker",
                    ["input"] = "perform the background lookup and return its output",
                    ["description"] = "lab background delegation",
                });
                Assert.Equal(function.Name, ((FunctionCallContent)call).Name);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var taskId = ParseTaskId(results, startCall.CallId);
            if (waitCall is null)
            {
                var function = RequireTool(WaitTool);
                AIContent call = new FunctionCallContent("lab-bg-wait-1", WaitTool, new Dictionary<string, object?>
                {
                    ["taskIds"] = new List<object?> { taskId },
                });
                Assert.Equal(function.Name, ((FunctionCallContent)call).Name);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            if (resultsCall is null)
            {
                var function = RequireTool(ResultsTool);
                AIContent call = new FunctionCallContent("lab-bg-results-1", ResultsTool, new Dictionary<string, object?>
                {
                    ["taskId"] = taskId,
                });
                Assert.Equal(function.Name, ((FunctionCallContent)call).Name);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var payload = results.FirstOrDefault(result => string.Equals(result.CallId, resultsCall.CallId, StringComparison.Ordinal))?.Result?.ToString()
                ?? "background payload";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"ack:lab-background-complete:{payload}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }

        private int ParseTaskId(FunctionResultContent[] results, string? startCallId)
        {
            var confirmation = results.FirstOrDefault(result => string.Equals(result.CallId, startCallId, StringComparison.Ordinal))?.Result?.ToString()
                ?? SeenResults.FirstOrDefault(text => text.Contains("Background task", StringComparison.Ordinal));
            if (confirmation is not null)
            {
                SpawnConfirmation = confirmation;
                var match = Regex.Match(confirmation, @"Background task (\d+)", RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var taskId))
                {
                    return taskId;
                }
            }

            throw new InvalidOperationException(
                $"Lab host could not parse the native spawn confirmation for task id. Seen: [{string.Join(" | ", SeenResults)}].");
        }
    }
}
