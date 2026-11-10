// <copyright file="NativeQuickstartLabTests.cs">
// Developer quickstart labs for every execution profile on MAF 1.27.0 (OC2-D-CONFIG-017/018/019/020).
// 017 Direct agent + one tool, no DB/broker/workers. 018 Harness native plan/todo + AgentSession
// multi-turn. 019 typed Workflow: sequential + parallel fan-in + HITL approve in one lab.
// 020 DurableWorkflow: passing test asserting the explicit unsupported diagnostic
// (MafCapabilityUnavailableException via ValidateExecutionProfile), not a fake run.
// No custom quickstart engine; deterministic stubs only, no network, no DB/broker.
// </copyright>

using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Agents.Templates;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>Executable developer quickstarts, one fact per drivable leg (017/018/019/020).</summary>
public sealed class NativeQuickstartLabTests
{
    private static readonly TimeSpan WorkflowTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HitlTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;

    public NativeQuickstartLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Quickstart_Direct_LeadAgent_With_One_Tool_Requires_No_Db_Broker_Or_Workers()
    {
        // 017: register lead + one helper via the real registration APIs, then run a
        // Direct turn with a single AIFunction tool on a deterministic stub.
        var services = new ServiceCollection();
        services.AddOpenConnectLeadAgent("quickstart-lead", builder => builder.Name = "QuickstartLead");
        services.AddOpenConnectAgent("quickstart-helper", builder => builder.Name = "QuickstartHelper");

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.FullName is string name &&
            (name.Contains("DbContext", StringComparison.Ordinal) ||
             name.Contains("Nats", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("JetStream", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Postgres", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Worker", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));

        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IHostedService>());

        var registry = provider.GetRequiredService<IAgentDefinitionRegistry>();
        var leadRegistration = provider.GetService<AgentRegistrationExtensions.LeadAgentRegistration>();
        Assert.NotNull(leadRegistration);
        var leadId = leadRegistration.AgentId;
        Assert.NotNull(registry.Get(leadId));
        Assert.NotNull(registry.Get(new AgentId("quickstart-helper")));

        var client = new ToolEchoChatClient();
        var factory = new MafRealAgentFactory(registry, client);
        var executions = 0;
        var tool = AIFunctionFactory.Create(
            (string value) => { executions++; return value + "-suffix"; },
            "append_suffix",
            "Append a suffix.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await factory.RunAsync(leadId, "hello", [tool], cts.Token);

        Assert.Equal(1, executions);
        Assert.Contains("hello-suffix", response.Text, StringComparison.Ordinal);
        Assert.Equal(2, client.Calls);
        _output.WriteLine($"Direct quickstart: lead={leadId.Value}, tool invoked once, no hosted/db/broker services.");
    }

    [Fact]
    public async Task Quickstart_Harness_Plan_Todo_Two_Turns_On_One_Session()
    {
        // 018: Harness quickstart via the native AsHarnessAgent factory + TodoProvider
        // state across two turns on a single AgentSession. Harness is Available on
        // MAF 1.27.0 (see MafCapabilityMatrixTests); the gate must pass first.
        OrchestrationProfileRouter.ValidateExecutionProfile("Harness");

        var services = new ServiceCollection();
        services.AddOpenConnectLeadAgent("quickstart-harness-lead", builder => builder.Name = "QuickstartHarness");
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentDefinitionRegistry>();
        Assert.NotNull(registry.Get(new AgentId("quickstart-harness-lead")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var todo = new TodoProvider(new TodoProviderOptions());
        var modes = new AgentModeProvider(new AgentModeProviderOptions());
        var options = new HarnessAgentOptions
        {
            Id = "quickstart-harness",
            Name = "QuickstartHarness",
            Description = "Deterministic quickstart plan/execute lab.",
            AIContextProviders = [todo, modes],
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = true,
        };

        var client = new QuickstartHarnessChatClient();
        HarnessAgent agent = client.AsHarnessAgent(options);

        AgentSession session = await agent.CreateSessionAsync(ct);
        Assert.NotNull(session);

        client.BeginPhase("plan");
        await modes.SetModeAsync(session, "plan", ct);
        AgentResponse planResponse = await agent.RunAsync("quickstart-turn-1: draft the plan", session, cancellationToken: ct);

        Assert.StartsWith("ack:quickstart-turn-1", planResponse.Text, StringComparison.Ordinal);
        Assert.Equal("plan", await modes.GetModeAsync(session, ct));
        var planned = await todo.GetAllTodosAsync(session, ct);
        Assert.Equal(2, planned.Count);
        Assert.Equal(2, (await todo.GetRemainingTodosAsync(session, ct)).Count);

        var todoIds = planned.Select(item => (object?)item.Id).ToList();
        client.BeginPhase("execute", todoIds);
        await modes.SetModeAsync(session, "execute", ct);
        AgentResponse executeResponse = await agent.RunAsync("quickstart-turn-2: execute the plan", session, cancellationToken: ct);

        Assert.StartsWith("ack:quickstart-turn-2", executeResponse.Text, StringComparison.Ordinal);
        Assert.Equal("execute", await modes.GetModeAsync(session, ct));
        Assert.Empty(await todo.GetRemainingTodosAsync(session, ct));
        var completed = await todo.GetAllTodosAsync(session, ct);
        Assert.Equal(2, completed.Count);
        Assert.All(completed, item => Assert.True(item.IsComplete));
        _output.WriteLine("Harness quickstart: two turns on one session, plan -> execute, 2/2 todos complete.");
    }

    [Fact]
    public async Task Quickstart_Typed_Workflow_Sequential_Parallel_And_Hitl_Nodes()
    {
        // 019: typed Workflow quickstart through the proven native path
        // (WorkflowBuilder + NativeWorkflowRunner). No AddOpenConnectWorkflow
        // registration API exists anywhere in src (grep verified), so the lab
        // goes through the native builder, exactly as prior labs do.
        OrchestrationProfileRouter.ValidateExecutionProfile("Workflow");

        // Sequential node pair: typed string edge upper -> exclaim.
        string? observedDownstreamInput = null;
        var upper = new FunctionExecutor<string, string>(
            "quickstart-upper",
            (input, _, _) => ValueTask.FromResult(input.ToUpperInvariant()));
        var exclaim = new FunctionExecutor<string, string>(
            "quickstart-exclaim",
            (input, _, _) =>
            {
                observedDownstreamInput = input;
                return ValueTask.FromResult(input + "!");
            });
        var sequential = new WorkflowBuilder(upper)
            .AddEdge(upper, exclaim)
            .WithOutputFrom(exclaim)
            .Build();

        var sequentialResult = await new NativeWorkflowRunner()
            .RunAsync(sequential, "hello")
            .WaitAsync(WorkflowTimeout);

        Assert.Equal("HELLO!", Assert.Single(sequentialResult.Outputs).As<string>());
        Assert.Equal("HELLO", observedDownstreamInput);

        // Parallel fan-in: fan-out to A/B, barrier merge yields one sorted output.
        var start = new FunctionExecutor<string, string>(
            "quickstart-fanout-start", (input, _, _) => ValueTask.FromResult(input));
        var branchA = new FunctionExecutor<string, string>(
            "quickstart-branch-a", (input, _, _) => ValueTask.FromResult(input + ":A"));
        var branchB = new FunctionExecutor<string, string>(
            "quickstart-branch-b", (input, _, _) => ValueTask.FromResult(input + ":B"));
        var merge = new FunctionExecutor<string>(
            "quickstart-merge",
            async (input, context, cancellationToken) =>
            {
                var parts = await context.ReadStateAsync<List<string>>("parts", cancellationToken)
                    ?? new List<string>();
                parts.Add(input);
                await context.QueueStateUpdateAsync("parts", parts, cancellationToken);
                if (parts.Count == 2)
                {
                    parts.Sort(StringComparer.Ordinal);
                    await context.YieldOutputAsync("merged:" + string.Join("+", parts), cancellationToken);
                }
            },
            null,
            null,
            [typeof(string)]);
        var parallel = new WorkflowBuilder(start)
            .AddFanOutEdge(start, [branchA, branchB])
            .AddFanInBarrierEdge([branchA, branchB], merge)
            .WithOutputFrom(merge)
            .Build();

        var parallelResult = await new NativeWorkflowRunner()
            .RunAsync(parallel, "go")
            .WaitAsync(WorkflowTimeout);

        Assert.Equal("merged:go:A+go:B", Assert.Single(parallelResult.Outputs).As<string>());

        // HITL node: external "approval" call halts; the authorized approve
        // response completes the run through the downstream executor.
        const string sessionId = "quickstart-hitl-approve";
        using var timeout = new CancellationTokenSource(HitlTimeout);
        var counter = new HitlCounter();
        var hitlWorkflow = CreateHitlWorkflow(counter);
        await using var run = await InProcessExecution.Default
            .RunStreamingAsync(hitlWorkflow, "start", sessionId, timeout.Token);

        var (pending, _) = await DrainHitlAsync(run, timeout.Token);
        Assert.NotNull(pending);
        Assert.Equal("approval", pending!.PortInfo.PortId);

        await run.SendResponseAsync(pending.CreateResponse("approved"));
        var (_, outputs) = await DrainHitlAsync(run, timeout.Token);

        var status = await run.GetStatusAsync(timeout.Token);
        Assert.True(status is RunStatus.Ended or RunStatus.Idle, $"Unexpected terminal status {status}.");
        Assert.Equal(1, Volatile.Read(ref counter.Value));
        Assert.Equal("completed:approved", Assert.Single(outputs));
        _output.WriteLine("Workflow quickstart: sequential + parallel fan-in + HITL approve all executed natively.");
    }

    [Fact]
    public void Quickstart_DurableWorkflow_Reports_Explicit_Unsupported_Diagnostic()
    {
        // 020: no durable backend ships in MAF 1.27.0
        // (MafRuntimeProbe: no "Durable"-named type exists), so the quickstart
        // leg is a passing test asserting the fail-closed diagnostic per the
        // Agents README Rule 2 — not a fake run.
        var exception = Assert.Throws<MafCapabilityUnavailableException>(
            () => OrchestrationProfileRouter.ValidateExecutionProfile("DurableWorkflow"));

        Assert.Equal("DurableWorkflow", exception.Profile);
        Assert.Equal("Microsoft.Agents.AI.Workflows.DurableWorkflowExecutor", exception.MissingTypeName);
        Assert.Contains("unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(exception.MissingTypeName, exception.Message, StringComparison.Ordinal);
        _output.WriteLine($"DurableWorkflow quickstart diagnostic: {exception.Message}");
    }

    private static Workflow CreateHitlWorkflow(HitlCounter counter)
    {
        var gate = new FunctionExecutor<string, object>(
            "quickstart-request-gate",
            (input, _context, _cancellationToken) => ValueTask.FromResult<object>(
                input == "start" ? (object)new QuickstartApprovalRequest(input) : (object)("completed:" + input)));
        var downstream = new FunctionExecutor<string, string>(
            "quickstart-downstream",
            (input, _context, _cancellationToken) =>
            {
                Interlocked.Increment(ref counter.Value);
                return ValueTask.FromResult(input);
            });
        return new WorkflowBuilder(gate)
            .AddExternalCall<QuickstartApprovalRequest, string>(gate, "approval")
            .AddEdge(gate, downstream)
            .WithOutputFrom(downstream)
            .Build();
    }

    private static async Task<(ExternalRequest? Pending, List<string> Outputs)> DrainHitlAsync(
        StreamingRun run, CancellationToken cancellationToken)
    {
        ExternalRequest? pending = null;
        var outputs = new List<string>();
        await foreach (var workflowEvent in run.WatchStreamAsync(
            blockOnPendingRequest: false, cancellationToken).ConfigureAwait(false))
        {
            if (workflowEvent is RequestInfoEvent requestInfo)
            {
                pending = requestInfo.Request;
            }
            else if (workflowEvent is WorkflowOutputEvent output)
            {
                var text = output.As<string>();
                Assert.NotNull(text);
                outputs.Add(text!);
            }
        }

        return (pending, outputs);
    }

    private sealed class HitlCounter
    {
        public int Value;
    }

    private sealed record QuickstartApprovalRequest(string Text);

    private sealed class ToolEchoChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("quickstart-direct");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1 && options?.Tools?.Count > 0)
            {
                var function = Assert.IsAssignableFrom<AIFunction>(options.Tools[0]);
                var call = new FunctionCallContent(
                    "quickstart-call-1", function.Name, new Dictionary<string, object?> { ["value"] = "hello" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();
            var text = result?.Result?.ToString() ?? messages.Last(message => message.Role == ChatRole.User).Text;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
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

    private sealed class QuickstartHarnessChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("quickstart-harness");

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        private string _phase = "plan";
        private IReadOnlyList<object?> _ids = [];
        private bool _emittedCall;
        private int _calls;

        public void BeginPhase(string phase, IReadOnlyList<object?>? ids = null)
        {
            _phase = phase;
            _ids = ids ?? [];
            _emittedCall = false;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            var tools = options?.Tools?.OfType<AIFunction>().ToArray() ?? [];
            _calls++;

            if (!_emittedCall && (_phase == "plan" || _phase == "execute"))
            {
                var target = _phase == "plan" ? "todos_add" : "todos_complete";
                var function = tools.FirstOrDefault(tool => string.Equals(tool.Name, target, StringComparison.Ordinal));
                if (function is null)
                {
                    throw new InvalidOperationException(
                        $"Quickstart harness expected native tool '{target}'.");
                }

                var args = _phase == "plan" ? BuildPlanArgs(function) : BuildCompleteArgs(function, _ids);
                _emittedCall = true;
                AIContent call = new FunctionCallContent($"quickstart-{_phase}-{_calls}", target, args);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var prompt = snapshot
                .LastOrDefault(message => message.Role == ChatRole.User &&
                    (message.Text ?? string.Empty).StartsWith("quickstart-turn-", StringComparison.Ordinal))?.Text ?? "quickstart-ack";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ack:" + prompt)));
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

        private static IDictionary<string, object?> BuildPlanArgs(AIFunction function)
        {
            foreach (var property in EnumerateProperties(function.JsonSchema))
            {
                if (IsArray(property.Value))
                {
                    var items = GetProperty(property.Value, "items");
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [property.Name] = new List<object?>
                        {
                            BuildTodoObject(items, "Draft quickstart plan", "Outline the deterministic verification steps."),
                            BuildTodoObject(items, "Execute quickstart run", "Run the harness agent and record the evidence."),
                        },
                    };
                }
            }

            throw new InvalidOperationException($"Tool '{function.Name}' has no array parameter.");
        }

        private static IDictionary<string, object?> BuildCompleteArgs(AIFunction function, IReadOnlyList<object?> ids)
        {
            foreach (var property in EnumerateProperties(function.JsonSchema))
            {
                if (IsArray(property.Value))
                {
                    var items = GetProperty(property.Value, "items");
                    return new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [property.Name] = ids.Select(id => BuildCompletionElement(items, id)).ToList(),
                    };
                }
            }

            throw new InvalidOperationException($"Tool '{function.Name}' has no array parameter.");
        }

        private static object? BuildTodoObject(JsonElement itemSchema, string title, string description)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in EnumerateProperties(itemSchema))
            {
                values[property.Name] = DefaultScalar(property.Name, property.Value);
            }

            if (values.Count == 0)
            {
                values["title"] = title;
                values["description"] = description;
                return values;
            }

            foreach (var key in values.Keys.ToArray())
            {
                if (key.Contains("title", StringComparison.OrdinalIgnoreCase))
                {
                    values[key] = title;
                }
                else if (key.Contains("descri", StringComparison.OrdinalIgnoreCase))
                {
                    values[key] = description;
                }
            }

            return values;
        }

        private static object? BuildCompletionElement(JsonElement items, object? id)
        {
            if (items.ValueKind == JsonValueKind.Object && items.TryGetProperty("properties", out _))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in EnumerateProperties(items))
                {
                    values[property.Name] = CompletionScalar(property.Name, property.Value, id);
                }

                return values;
            }

            return CoerceId(items, id);
        }

        private static object? CompletionScalar(string name, JsonElement schema, object? id)
        {
            if (name.Contains("reason", StringComparison.OrdinalIgnoreCase))
            {
                return "Verified done in the deterministic quickstart run.";
            }

            if (name.Contains("id", StringComparison.OrdinalIgnoreCase))
            {
                return CoerceId(schema, id);
            }

            return DefaultScalar(name, schema);
        }

        private static object? DefaultScalar(string name, JsonElement schema)
        {
            return GetTypeName(schema) switch
            {
                "integer" => (object?)0L,
                "number" => 0d,
                "boolean" => false,
                _ => "quickstart-" + name,
            };
        }

        private static object? CoerceId(JsonElement schema, object? id)
        {
            if (id is null)
            {
                return "quickstart-id";
            }

            if (string.Equals(GetTypeName(schema), "integer", StringComparison.Ordinal))
            {
                var text = Convert.ToString(id, CultureInfo.InvariantCulture);
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    return number;
                }
            }

            return Convert.ToString(id, CultureInfo.InvariantCulture);
        }

        private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement schema)
        {
            if (schema.ValueKind == JsonValueKind.Object &&
                schema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    yield return property;
                }
            }
        }

        private static JsonElement GetProperty(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
                ? value
                : default;
        }

        private static string? GetTypeName(JsonElement schema)
        {
            return schema.ValueKind == JsonValueKind.Object &&
                schema.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }

        private static bool IsArray(JsonElement schema)
        {
            return string.Equals(GetTypeName(schema), "array", StringComparison.Ordinal);
        }
    }
}
