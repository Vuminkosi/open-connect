using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Agents.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using Xunit;

namespace OpenConnect.LiveUI.Agents.Tests;

public sealed class LiveUIAgentToolsTests
{
    private static readonly LiveUIManifest Manifest = CreateManifest();

    [Fact]
    public async Task ToolFactory_exposes_typed_MAF_functions_and_execution_uses_host_context()
    {
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var service = CreateTools(authorizer, runtime);
        var tools = LiveUIAgentToolFactory.CreateTools(service);

        Assert.Equal(
            ["liveui_get_current_page", "liveui_navigate", "liveui_inspect", "liveui_execute", "liveui_await_state", "liveui_highlight", "liveui_clear_highlight"],
            tools.Select(tool => tool.Name));

        var pageFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_get_current_page"));
        var pageResult = Assert.IsType<System.Text.Json.JsonElement>(await pageFunction.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal("todo.home", pageResult.GetProperty("routeId").GetString());

        var navigate = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_navigate"));
        var response = await navigate.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["routeId"] = "todo.settings"
        }));

        var receipt = System.Text.Json.JsonSerializer.Deserialize<LiveUIActionReceipt>(Assert.IsType<System.Text.Json.JsonElement>(response));
        Assert.NotNull(receipt);
        Assert.Equal(LiveUIActionOutcome.Completed, receipt.Outcome);
        var action = Assert.IsType<LiveUIAction>(runtime.LastContext!.Action);
        Assert.Equal("trusted-session", action.SessionId);
        Assert.Equal("todo.home", action.RouteId);
        Assert.Equal("todo.settings", action.Parameters.DestinationRouteId);
        Assert.Equal(LiveUIActionType.Navigate, action.Type);
    }

    [Fact]
    public async Task Current_page_contains_only_targets_with_authorized_capabilities()
    {
        var authorizer = new RecordingAuthorizer((_, context) =>
            context.Target?.Id != "todo.secret" && context.Action.Type != LiveUIActionType.SetValue);
        var service = CreateTools(authorizer, new RecordingRuntime());

        var page = await service.GetCurrentPageAsync();

        Assert.NotNull(page);
        Assert.Equal("todo.home", page.RouteId);
        Assert.Equal(4, page.Targets.Count);
        var search = Assert.Single(page.Targets, target => target.TargetId == "todo.search");
        Assert.Contains(LiveUICapability.Highlight, search.Capabilities);
        Assert.DoesNotContain(LiveUICapability.SetValue, search.Capabilities);
        var tab = Assert.Single(page.Targets, target => target.TargetId == "todo.completed");
        Assert.Contains(LiveUICapability.Activate, tab.Capabilities);
        var list = Assert.Single(page.Targets, target => target.TargetId == "todo.list");
        Assert.Contains(LiveUICapability.WaitForState, list.Capabilities);
        var region = Assert.Single(page.Targets, target => target.TargetId == "todo.region");
        Assert.Contains(LiveUICapability.WaitForState, region.Capabilities);
        Assert.DoesNotContain(authorizer.Contexts, item => item.Action.SessionId != "trusted-session");
        Assert.Equal("todo.home", Assert.Single(page.NavigableRoutes, item => item.RouteId == "todo.home").RouteId);
        Assert.Contains(page.NavigableRoutes, item => item.RouteId == "todo.settings");
    }

    [Fact]
    public async Task Current_page_omits_routes_denied_by_navigation_policy()
    {
        var authorizer = new RecordingAuthorizer((_, context) =>
            context.Action.Type != LiveUIActionType.Navigate || context.Action.Parameters.DestinationRouteId != "todo.settings");
        var service = CreateTools(authorizer, new RecordingRuntime());

        var page = await service.GetCurrentPageAsync();

        Assert.NotNull(page);
        Assert.Single(page.NavigableRoutes);
        Assert.DoesNotContain(page.NavigableRoutes, item => item.RouteId == "todo.settings");
    }

    [Fact]
    public async Task Current_page_returns_safe_navigation_catalog_when_current_route_is_not_inspectable()
    {
        var authorizer = new RecordingAuthorizer((_, context) =>
            context.Action.Type == LiveUIActionType.Navigate
                ? context.Action.Parameters.DestinationRouteId == "todo.home"
                : context.Route.Id == "todo.home");
        var tools = new LiveUIAgentTools(
            new ContextAccessor(new LiveUIAgentContext("trusted-session", "todo.settings")),
            new ManifestProvider(Manifest), authorizer, new LiveUIManifestValidator(),
            CreateOrchestrator(authorizer, new RecordingRuntime()), Options.Create(new LiveUIOptions()));

        var page = await tools.GetCurrentPageAsync();

        Assert.NotNull(page);
        Assert.Equal("todo.settings", page.RouteId);
        Assert.Empty(page.Targets);
        Assert.Equal("todo.home", Assert.Single(page.NavigableRoutes).RouteId);
    }

    [Fact]
    public async Task Client_tool_factory_uses_host_session_and_returns_structured_client_receipts()
    {
        var broker = new RecordingInteractionBroker((session, request, resultKind) =>
        {
            Assert.Equal("01ARZ3NDEKTSV4RRFFQ69G5FAV", session.Value.ToString());
            var payload = request.Operation == "get_current_page"
                ? "{\"routeId\":\"todo.home\"}"
                : "{\"outcome\":\"Completed\",\"postconditionVerified\":true}";
            return Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, resultKind, payload));
        });
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));

        var pageFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_get_current_page"));
        var page = Assert.IsType<System.Text.Json.JsonElement>(await pageFunction.InvokeAsync(new AIFunctionArguments()));
        Assert.Equal("todo.home", page.GetProperty("routeId").GetString());

        var executeFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));
        var receipt = Assert.IsType<System.Text.Json.JsonElement>(await executeFunction.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" })));
        Assert.Equal("Completed", receipt.GetProperty("outcome").GetString());

        var request = Assert.Single(broker.Requests, item => item.Operation == "execute");
        Assert.Equal("todo.completed", request.TargetId);
        Assert.Equal("SelectTab", request.Action);

        var navigateFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_navigate"));
        _ = await navigateFunction.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["routeId"] = "todo.home" }));
        var inspectFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_inspect"));
        _ = await inspectFunction.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.list" }));
        var awaitFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_await_state"));
        _ = await awaitFunction.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["stateName"] = "selected" }));
        var highlightFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_highlight"));
        _ = await highlightFunction.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.list" }));
        var clearFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_clear_highlight"));
        _ = await clearFunction.InvokeAsync(new AIFunctionArguments());

        Assert.Equal("todo.home", Assert.Single(broker.Requests, item => item.Operation == "navigate").RouteId);
        Assert.Equal("todo.list", Assert.Single(broker.Requests, item => item.Operation == "inspect").TargetId);
        var waitRequest = Assert.Single(broker.Requests, item => item.Operation == "await_state");
        Assert.Equal("todo.completed", waitRequest.TargetId);
        Assert.Equal("selected", waitRequest.StateName);
        Assert.Equal("todo.list", Assert.Single(broker.Requests, item => item.Operation == "highlight").TargetId);
        Assert.Single(broker.Requests, item => item.Operation == "clear_highlight");
        Assert.All(broker.Requests, item => Assert.True(Guid.TryParseExact(item.CorrelationId, "D", out _)));
    }

    [Fact]
    public async Task Client_tool_factory_rejects_untrusted_response_correlation()
    {
        var broker = new RecordingInteractionBroker((_, _, _) => Task.FromResult<ClientInteractionResponse?>(
            new(Guid.NewGuid().ToString("D"), "page", "{}")));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var pageFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_get_current_page"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await pageFunction.InvokeAsync(new AIFunctionArguments()));

        Assert.Equal("client_response_mismatch", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MafRealAgentFactory_waits_for_the_connected_client_tool_receipt()
    {
        var broker = new RecordingInteractionBroker((session, request, resultKind) =>
        {
            Assert.Equal("01ARZ3NDEKTSV4RRFFQ69G5FAV", session.Value.ToString());
            return Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, resultKind,
                "{\"outcome\":\"Completed\",\"postconditionVerified\":true}"));
        });
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var directFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));
        _ = await directFunction.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" }));
        broker.Requests.Clear();
        var agentId = new AgentId("liveui-client-assistant");
        var client = new ClientBridgeToolCallChatClient();
        var factory = new MafRealAgentFactory(new TestAgentRegistry(new AgentDefinition(agentId, "LiveUI")), client);

        var response = await factory.RunAsync(agentId, "show completed tasks", tools, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Contains("Completed", response.Text, StringComparison.Ordinal);
        var request = Assert.Single(broker.Requests);
        Assert.Equal("todo.completed", request.TargetId);
        Assert.Equal("SelectTab", request.Action);
    }

    [Fact]
    public async Task Missing_host_context_denies_without_running_browser_action()
    {
        var runtime = new RecordingRuntime();
        var service = new LiveUIAgentTools(new ContextAccessor(null), new ManifestProvider(Manifest),
            new RecordingAuthorizer(), new LiveUIManifestValidator(), CreateOrchestrator(new RecordingAuthorizer(), runtime),
            Options.Create(new LiveUIOptions()));

        var result = await service.HighlightAsync("todo.search");

        Assert.Equal(LiveUIActionOutcome.Denied, result.Outcome);
        Assert.Equal("session_context_unavailable", result.Code);
        Assert.Null(runtime.LastContext);
    }

    [Fact]
    public void Creating_MAF_tools_registers_ready_tool_capability()
    {
        var registry = new RecordingCapabilityRegistry();
        var manifestProvider = new ManifestProvider(Manifest);
        var authorizer = new RecordingAuthorizer();
        var tools = new LiveUIAgentTools(new ContextAccessor(new LiveUIAgentContext("trusted-session", "todo.home")),
            manifestProvider, authorizer, new LiveUIManifestValidator(), CreateOrchestrator(authorizer, new RecordingRuntime()),
            Options.Create(new LiveUIOptions()), registry);

        _ = LiveUIAgentToolFactory.CreateTools(tools);

        Assert.Equal(CapabilityKind.Tool, Assert.Single(registry.Registered).Kind);
        Assert.Equal("liveui.agent-tools", registry.Registered[0].Id.Value);
        Assert.Equal(DetectorState.Ready, registry.Registered[0].State);
    }

    [Fact]
    public async Task MafRealAgentFactory_invokes_liveui_tool_and_returns_to_the_model_loop()
    {
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var liveUiTools = LiveUIAgentToolFactory.CreateTools(CreateTools(authorizer, runtime));
        var agentId = new AgentId("liveui-assistant");
        var client = new ToolCallChatClient();
        var factory = new MafRealAgentFactory(new TestAgentRegistry(new AgentDefinition(agentId, "LiveUI")), client);

        var response = await factory.RunAsync(agentId, "open settings", liveUiTools, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Contains("verified", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(LiveUIActionType.Navigate, runtime.LastContext!.Action.Type);
        Assert.Equal("todo.settings", runtime.LastContext.Action.Parameters.DestinationRouteId);
        Assert.Equal("trusted-session", runtime.LastContext.Action.SessionId);
    }

    private static LiveUIAgentTools CreateTools(RecordingAuthorizer authorizer, RecordingRuntime runtime)
    {
        var orchestrator = CreateOrchestrator(authorizer, runtime);
        return new LiveUIAgentTools(new ContextAccessor(new LiveUIAgentContext("trusted-session", "todo.home")),
            new ManifestProvider(Manifest), authorizer, new LiveUIManifestValidator(), orchestrator,
            Options.Create(new LiveUIOptions()));
    }

    private static LiveUIOrchestrator CreateOrchestrator(RecordingAuthorizer authorizer, RecordingRuntime runtime) =>
        new(new ManifestProvider(Manifest), authorizer, runtime, Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-1", string.Empty,
        [
            new LiveUIRoute("todo.home", "/", "Todo",
            [
                new LiveUITarget("todo.search", LiveUITargetKind.Input, "Search tasks",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.SetValue, LiveUICapability.Highlight }),
                new LiveUITarget("todo.secret", LiveUITargetKind.Input, "Private notes",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.SetValue }),
                new LiveUITarget("todo.completed", LiveUITargetKind.Tab, "Completed",
                    new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Filter }),
                new LiveUITarget("todo.list", LiveUITargetKind.List, "Task list",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.WaitForState }),
                new LiveUITarget("todo.region", LiveUITargetKind.Region, "todo-list",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.WaitForState }),
            ]),
            new LiveUIRoute("todo.settings", "/settings", "Settings", [])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private sealed class ContextAccessor(LiveUIAgentContext? context) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context);
    }

    private sealed class ManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class RecordingAuthorizer(Func<RecordingAuthorizer, LiveUIExecutionContext, bool>? allow = null) : ILiveUIActionAuthorizer
    {
        public List<LiveUIExecutionContext> Contexts { get; } = [];
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            var allowed = allow?.Invoke(this, context) ?? true;
            return ValueTask.FromResult(new LiveUIAuthorizationDecision(allowed, allowed ? "allowed" : "denied"));
        }
    }

    private sealed class RecordingInteractionBroker(
        Func<SessionId, ClientInteractionRequest, string, Task<ClientInteractionResponse?>> respond) : IClientInteractionBroker
    {
        public List<ClientInteractionRequest> Requests { get; } = [];

        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return respond(sessionId, request, expectedResultKind);
        }
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
        public LiveUIExecutionContext? LastContext { get; private set; }
        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "verified", true));
        }
    }

    private sealed class RecordingCapabilityRegistry : ICapabilityRegistry
    {
        public List<CapabilityDescriptor> Registered { get; } = [];
        public CapabilityDescriptor? Get(CapabilityId capabilityId) => Registered.SingleOrDefault(item => item.Id == capabilityId);
        public IReadOnlyList<CapabilityDescriptor> GetReady(CapabilityKind kind) => Registered.Where(item => item.Kind == kind && item.State == DetectorState.Ready).ToArray();
        public void Register(CapabilityDescriptor descriptor) => Registered.Add(descriptor);
        public void MarkStale(CapabilityId capabilityId) { }
    }

    private sealed class TestAgentRegistry(AgentDefinition definition) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => id == definition.Id ? definition : null;
        public IReadOnlyList<AgentDefinition> GetAll() => [definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class ToolCallChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("liveui-agent-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1)
            {
                var function = Assert.IsAssignableFrom<AIFunction>(options!.Tools!.Single(tool => tool.Name == "liveui_navigate"));
                var call = new FunctionCallContent("liveui-call-1", function.Name,
                    new Dictionary<string, object?> { ["routeId"] = "todo.settings" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, result?.Result?.ToString() ?? "no tool receipt")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ClientBridgeToolCallChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("liveui-client-bridge-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls == 1)
            {
                var function = Assert.IsAssignableFrom<AIFunction>(options!.Tools!.Single(tool => tool.Name == "liveui_execute"));
                var call = new FunctionCallContent("liveui-client-call", function.Name,
                    new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                result?.Result?.ToString() ?? "missing client receipt")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
