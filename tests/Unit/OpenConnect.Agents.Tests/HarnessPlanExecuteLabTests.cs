// <copyright file="HarnessPlanExecuteLabTests.cs">
// Deterministic native Harness plan/execute lab (MAF 1.27.0).
// Plan state is the native Microsoft.Agents.AI.TodoState
// (TodoItem / TodoProvider tools todos_add + todos_complete) and mode state is
// the native Microsoft.Agents.AI.AgentModeState ("plan" / "execute").
// </copyright>

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Agents.Execution;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Agents.Tests;

/// <summary>Proves native Harness plan/execute on MAF 1.27.0 with a deterministic <see cref="IChatClient"/>.</summary>
public sealed class HarnessPlanExecuteLabTests
{
    private const string HarnessAgentTypeName = "Microsoft.Agents.AI.HarnessAgent";

    private readonly ITestOutputHelper _output;

    public HarnessPlanExecuteLabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Harness_plan_execute_lab_proves_native_todo_and_mode_state()
    {
        RequireHarnessApi(_output);
        OrchestrationProfileRouter.ValidateExecutionProfile("Harness");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = cts.Token;

        var todo = new TodoProvider(new TodoProviderOptions());
        var modes = new AgentModeProvider(new AgentModeProviderOptions());
        var options = new HarnessAgentOptions
        {
            Id = "lab-harness",
            Name = "LabHarness",
            Description = "Deterministic harness plan/execute lab.",
            AIContextProviders = [todo, modes],
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableOpenTelemetry = true,
        };
        _output.WriteLine("HarnessAgentOptions: AIContextProviders=[TodoProvider, AgentModeProvider], built-ins disabled (no network, in-memory history, no compaction).");

        var client = new LabHarnessChatClient2();
        HarnessAgent agent = client.AsHarnessAgent(options);
        Assert.Equal(HarnessAgentTypeName, agent.GetType().FullName);

        AgentSession session = await agent.CreateSessionAsync(ct);
        Assert.NotNull(session);

        client.BeginPhase("plan");
        await modes.SetModeAsync(session, "plan", ct);
        AgentResponse planResponse = await agent.RunAsync("lab-turn-1: draft the verification plan", session, cancellationToken: ct);

        Assert.StartsWith("ack:lab-turn-1", planResponse.Text, StringComparison.Ordinal);
        Assert.Equal("plan", await modes.GetModeAsync(session, ct));
        var planned = await todo.GetAllTodosAsync(session, ct);
        Assert.Equal(2, planned.Count);
        Assert.Contains(planned, item => item.Title == "Draft lab plan");
        Assert.Contains(planned, item => item.Title == "Execute lab run");
        Assert.Equal(2, (await todo.GetRemainingTodosAsync(session, ct)).Count);
        Assert.Contains(client.ToolsPerCall, entry => entry.Split(',').Contains("todos_add", StringComparer.Ordinal));
        _output.WriteLine($"Plan turn: mode=plan, todos=[{string.Join("; ", planned.Select(item => $"{item.Id}:{item.Title}"))}].");

        var todoIds = planned.Select(item => (object?)item.Id).ToList();
        client.BeginPhase("execute", todoIds);
        await modes.SetModeAsync(session, "execute", ct);
        AgentResponse executeResponse = await agent.RunAsync("lab-turn-2: execute the verification plan", session, cancellationToken: ct);

        Assert.StartsWith("ack:lab-turn-2", executeResponse.Text, StringComparison.Ordinal);
        Assert.Equal("execute", await modes.GetModeAsync(session, ct));
        var remaining = await todo.GetRemainingTodosAsync(session, ct);
        Assert.Empty(remaining);
        var completed = await todo.GetAllTodosAsync(session, ct);
        Assert.Equal(2, completed.Count);
        Assert.All(completed, item => Assert.True(item.IsComplete));
        Assert.Contains(client.ToolsPerCall, entry => entry.Split(',').Contains("todos_complete", StringComparer.Ordinal));
        _output.WriteLine($"Execute turn: mode=execute, remaining={remaining.Count}, completed={completed.Count}.");

        var turn2First = client.Requests.First(messages =>
            messages.Any(message => message.Role == ChatRole.User &&
                (message.Text ?? string.Empty).Contains("lab-turn-2", StringComparison.Ordinal)));
        Assert.Contains(turn2First, message => message.Role == ChatRole.User &&
            (message.Text ?? string.Empty).Contains("lab-turn-1", StringComparison.Ordinal));
        _output.WriteLine($"Inner model calls: {client.Requests.Count}; turn-2 first request carries both turn prompts.");

        AgentSession otherSession = await agent.CreateSessionAsync(ct);
        Assert.NotSame(session, otherSession);
        Assert.Empty(await todo.GetAllTodosAsync(otherSession, ct));
        var otherMode = await modes.GetModeAsync(otherSession, ct);
        _output.WriteLine($"Fresh session: todos=0, mode={otherMode}; original retains mode=execute with 2 completed todos.");
        Assert.Equal("plan", otherMode);
    }

    [Fact]
    public void Harness_options_surface_matches_expected_members()
    {
        var harness = RequireHarnessApi(_output);
        var optionsType = harness.GetType("Microsoft.Agents.AI.HarnessAgentOptions");
        Assert.NotNull(optionsType);
        var names = optionsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        _output.WriteLine("HarnessAgentOptions members: " + string.Join(", ", names));

        foreach (var expected in new[]
        {
            "Id", "Name", "Description", "AIContextProviders", "ChatOptions", "HarnessInstructions",
            "DisableTodoProvider", "DisableAgentModeProvider", "DisableAgentSkillsProvider",
            "DisableFileMemory", "DisableWebSearch", "DisableOpenTelemetry",
            "MaximumIterationsPerRequest",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    private static Assembly RequireHarnessApi(ITestOutputHelper output)
    {
        Assembly harness;
        try
        {
            harness = Assembly.Load("Microsoft.Agents.AI.Harness");
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new MafCapabilityUnavailableException("Harness", HarnessAgentTypeName);
        }

        var agentType = harness.GetType(HarnessAgentTypeName);
        var optionsType = harness.GetType("Microsoft.Agents.AI.HarnessAgentOptions");
        var factory = harness.GetType("Microsoft.Extensions.AI.ChatClientHarnessExtensions")
            ?.GetMethod("AsHarnessAgent", BindingFlags.Public | BindingFlags.Static);
        if (agentType is null || optionsType is null || factory is null)
        {
            throw new MafCapabilityUnavailableException("Harness", HarnessAgentTypeName);
        }

        output.WriteLine($"Harness assembly: {harness.GetName().Name} {harness.GetName().Version}");
        output.WriteLine($"Harness agent FQN: {agentType.FullName} : {agentType.BaseType?.FullName}");
        output.WriteLine($"Harness factory: {factory}");
        return harness;
    }

    private sealed class LabHarnessChatClient2 : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("harness-lab");

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<string> ToolsPerCall { get; } = [];

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
            ToolsPerCall.Add(string.Join(",", tools.Select(tool => tool.Name)));
            _calls++;

            if (!_emittedCall && (_phase == "plan" || _phase == "execute"))
            {
                var target = _phase == "plan" ? "todos_add" : "todos_complete";
                var function = tools.FirstOrDefault(tool => string.Equals(tool.Name, target, StringComparison.Ordinal));
                if (function is null)
                {
                    throw new InvalidOperationException(
                        $"Lab harness expected native tool '{target}' but the pipeline supplied [{ToolsPerCall[^1]}].");
                }

                var args = _phase == "plan" ? BuildPlanArgs(function) : BuildCompleteArgs(function, _ids);
                _emittedCall = true;
                AIContent call = new FunctionCallContent($"lab-{_phase}-{_calls}", target, args);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var prompt = snapshot
                .LastOrDefault(message => message.Role == ChatRole.User &&
                    (message.Text ?? string.Empty).StartsWith("lab-turn-", StringComparison.Ordinal))?.Text ?? "lab-ack";
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
                            BuildTodoObject(items, "Draft lab plan", "Outline the deterministic lab verification steps."),
                            BuildTodoObject(items, "Execute lab run", "Run the harness agent and record the evidence."),
                        },
                    };
                }
            }

            throw new InvalidOperationException(
                $"Lab harness tool '{function.Name}' has no array parameter. Schema: {function.JsonSchema}");
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

            throw new InvalidOperationException(
                $"Lab harness tool '{function.Name}' has no array parameter. Schema: {function.JsonSchema}");
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
                return "Verified done in the deterministic lab run.";
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
                _ => "lab-" + name,
            };
        }

        private static object? CoerceId(JsonElement schema, object? id)
        {
            if (id is null)
            {
                return "lab-id";
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
