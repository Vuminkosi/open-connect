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
    public async Task Scoped_target_is_not_exposed_when_its_parent_is_unauthorized()
    {
        var parent = new LiveUITarget("parent", LiveUITargetKind.Region, "Private section",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect });
        var child = new LiveUITarget("child", LiveUITargetKind.Heading, "Configuration",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect }, ParentId: "parent");
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build", "", [new LiveUIRoute("home", "/", "Home", [parent, child])]);
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var provider = new ManifestProvider(manifest);
        var authorizer = new RecordingAuthorizer((_, context) => context.Target?.Id != "parent");
        var options = Options.Create(new LiveUIOptions());
        var orchestrator = new LiveUIOrchestrator(provider, authorizer, new RecordingRuntime(), options, new LiveUIManifestValidator());
        using var tools = new LiveUIAgentTools(new ContextAccessor(new LiveUIAgentContext("session", "home")), provider,
            authorizer, new LiveUIManifestValidator(), orchestrator, options);

        var page = await tools.GetCurrentPageAsync();

        Assert.NotNull(page);
        Assert.Empty(page.Targets);
    }

    [Fact]
    public async Task Failed_runtime_refresh_does_not_expose_stale_discovered_targets()
    {
        var stale = new LiveUITarget(LiveUIRuntimeTargetIds.Prefix + "old", LiveUITargetKind.Heading, "Previous page",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect });
        var replacement = Manifest with { Routes = Manifest.Routes.Select(route => route.Id == "todo.home"
            ? route with { Targets = route.Targets.Append(stale).ToArray() } : route).ToArray() };
        replacement = replacement with { ContentHash = LiveUIManifestHasher.ComputeContentHash(replacement) };
        var provider = new MutableManifestProvider(replacement);
        var authorizer = new RecordingAuthorizer();
        var options = Options.Create(new LiveUIOptions());
        var orchestrator = new LiveUIOrchestrator(provider, authorizer, new RecordingRuntime(), options, new LiveUIManifestValidator());
        using var tools = new LiveUIAgentTools(new ContextAccessor(new LiveUIAgentContext("session", "todo.home") { CurrentPath = "/" }),
            provider, authorizer, new LiveUIManifestValidator(), orchestrator, options, runtimeDiscovery: new FailedDiscovery());

        var page = await tools.GetCurrentPageAsync();

        Assert.NotNull(page);
        Assert.Contains(page.Targets, target => target.TargetId == "todo.search");
        Assert.DoesNotContain(page.Targets, target => target.TargetId == stale.Id);
        Assert.DoesNotContain(authorizer.Contexts, context => context.Target?.Id == stale.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_verified_navigation_advances_the_observed_page(bool verified)
    {
        var target = new LiveUITarget("destination.heading", LiveUITargetKind.Heading, "Destination",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect });
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build", "", [
            new LiveUIRoute("start", "/", "Start", []), new LiveUIRoute("destination", "/destination", "Destination", [target])]);
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var provider = new ManifestProvider(manifest);
        var accessor = new MutableContextAccessor { Current = new LiveUIAgentContext("session", "start") { CurrentPath = "/" } };
        var authorizer = new RecordingAuthorizer();
        var runtime = new NavigationRuntime(accessor, verified);
        var options = Options.Create(new LiveUIOptions());
        var orchestrator = new LiveUIOrchestrator(provider, authorizer, runtime, options, new LiveUIManifestValidator());
        using var tools = new LiveUIAgentTools(accessor, provider, authorizer, new LiveUIManifestValidator(), orchestrator, options);
        Assert.NotNull(await tools.GetCurrentPageAsync());

        var navigation = await tools.NavigateAsync("destination");
        var inspection = await tools.InspectAsync(target.Id);

        Assert.Equal(verified ? LiveUIActionOutcome.Completed : LiveUIActionOutcome.Uncertain, navigation.Outcome);
        Assert.Equal(verified ? LiveUIActionOutcome.Completed : LiveUIActionOutcome.Conflict, inspection.Outcome);
    }

    [Fact]
    public async Task Compiled_target_requires_a_fresh_page_after_the_concrete_route_changes()
    {
        var target = new LiveUITarget("docs.heading", LiveUITargetKind.Heading, "Installation",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect });
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build", "", [
            new LiveUIRoute("docs", "/docs/{version}/{slug}", "Docs", [target])]);
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var provider = new ManifestProvider(manifest);
        var accessor = new MutableContextAccessor { Current = new LiveUIAgentContext("session", "docs")
            { CurrentPath = "/docs/v1/install" } };
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var options = Options.Create(new LiveUIOptions());
        var orchestrator = new LiveUIOrchestrator(provider, authorizer, runtime, options, new LiveUIManifestValidator());
        using var tools = new LiveUIAgentTools(accessor, provider, authorizer, new LiveUIManifestValidator(), orchestrator, options);
        Assert.NotNull(await tools.GetCurrentPageAsync());
        accessor.Current = accessor.Current with { CurrentPath = "/docs/v1/configuration" };

        var stale = await tools.InspectAsync(target.Id);
        Assert.Equal(LiveUIActionOutcome.Conflict, stale.Outcome);
        Assert.Equal("page_context_stale", stale.Code);
        Assert.Equal(0, runtime.Calls);

        Assert.NotNull(await tools.GetCurrentPageAsync());
        Assert.Equal(LiveUIActionOutcome.Completed, (await tools.InspectAsync(target.Id)).Outcome);
        Assert.Equal(1, runtime.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Runtime_discovery_requires_route_inspection_and_filters_each_discovered_capability(bool allowRoute)
    {
        var provider = new MutableManifestProvider(Manifest);
        var discovery = new RecordingDiscovery(provider);
        var authorizer = new RecordingAuthorizer((_, context) => context.Target is null
            ? allowRoute
            : context.Target.Id == LiveUIRuntimeTargetIds.Prefix + "discovered" && context.Action.Type == LiveUIActionType.Inspect);
        var options = Options.Create(new LiveUIOptions());
        var orchestrator = new LiveUIOrchestrator(provider, authorizer, new RecordingRuntime(), options, new LiveUIManifestValidator());
        using var tools = new LiveUIAgentTools(new ContextAccessor(new LiveUIAgentContext("session", "todo.home")),
            provider, authorizer, new LiveUIManifestValidator(), orchestrator, options, runtimeDiscovery: discovery);

        var page = await tools.GetCurrentPageAsync();

        Assert.NotNull(page);
        Assert.Equal(allowRoute ? 1 : 0, discovery.Calls);
        if (allowRoute)
        {
            var target = Assert.Single(page.Targets, item => item.TargetId == LiveUIRuntimeTargetIds.Prefix + "discovered");
            Assert.Equal([LiveUICapability.Inspect], target.Capabilities);
            var directTool = Assert.IsAssignableFrom<AIFunction>(LiveUIAgentToolFactory.CreateTools(tools)
                .Single(item => item.Name == "liveui_get_current_page"));
            var directResult = Assert.IsType<System.Text.Json.JsonElement>(await directTool.InvokeAsync(new AIFunctionArguments()));
            Assert.Empty(directResult.GetProperty("targets").EnumerateArray());
            Assert.Equal(string.Empty, directResult.GetProperty("contentHash").GetString());
            Assert.Equal(1, discovery.Calls);
        }
        else Assert.Empty(page.Targets);
    }

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
        Assert.Contains("call liveui_get_current_page again", navigate.Description, StringComparison.Ordinal);
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
    public async Task Current_page_keeps_concrete_path_private_and_omits_unresolved_navigation_templates()
    {
        var target = new LiveUITarget("docs.heading", LiveUITargetKind.Heading, "Installation",
            new HashSet<LiveUICapability> { LiveUICapability.Inspect });
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build", "", [
            new LiveUIRoute("docs.page", "/docs/{Version}/{Slug}", "Document", [target]),
            new LiveUIRoute("docs.index", "/docs", "Documentation", [])]);
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var provider = new ManifestProvider(manifest);
        var orchestrator = new LiveUIOrchestrator(provider, authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());
        var context = new LiveUIAgentContext("session", "docs.page") { CurrentPath = "/docs/v1/installation" };
        using var tools = new LiveUIAgentTools(new ContextAccessor(context), provider, authorizer,
            new LiveUIManifestValidator(), orchestrator, Options.Create(new LiveUIOptions()));

        var page = await tools.GetCurrentPageAsync();
        Assert.Equal("/docs/{Version}/{Slug}", page?.Path);
        Assert.DoesNotContain(page!.NavigableRoutes, item => item.RouteId == "docs.page");
        Assert.Contains(page.NavigableRoutes, item => item.RouteId == "docs.index");
        var receipt = await tools.InspectAsync(target.Id);
        Assert.Equal(LiveUIActionOutcome.Completed, receipt.Outcome);
        Assert.Equal("/docs/v1/installation", runtime.LastContext!.Action.ExpectedPath);
    }

    [Fact]
    public async Task Tenant_policy_denies_every_liveui_tool_for_a_session_owned_by_another_tenant()
    {
        var sessionTenants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenant-a-session"] = "tenant-a",
            ["tenant-b-session"] = "tenant-b"
        };
        var authorizer = new RecordingAuthorizer((_, context) =>
            sessionTenants.TryGetValue(context.Action.SessionId, out var ownerTenant) && ownerTenant == "tenant-a");
        var runtime = new RecordingRuntime();
        var tools = new LiveUIAgentTools(
            new ContextAccessor(new LiveUIAgentContext("tenant-b-session", "todo.home")),
            new ManifestProvider(Manifest), authorizer, new LiveUIManifestValidator(),
            CreateOrchestrator(authorizer, runtime), Options.Create(new LiveUIOptions()));

        var page = await tools.GetCurrentPageAsync();
        var receipts = new[]
        {
            await tools.NavigateAsync("todo.settings"),
            await tools.InspectAsync("todo.list"),
            await tools.ExecuteAsync("todo.completed", "SelectTab"),
            await tools.AwaitStateAsync("todo.list", "textContains", "Report"),
            await tools.HighlightAsync("todo.list"),
            await tools.ClearHighlightAsync()
        };

        Assert.NotNull(page);
        Assert.Empty(page.Targets);
        Assert.Empty(page.NavigableRoutes);
        Assert.All(receipts, receipt => Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome));
        Assert.NotEmpty(authorizer.Contexts);
        Assert.All(authorizer.Contexts, context => Assert.Equal("tenant-b-session", context.Action.SessionId));
        Assert.Contains(authorizer.Contexts, context => context.Action.Type == LiveUIActionType.Navigate);
        Assert.Contains(authorizer.Contexts, context => context.Action.Type == LiveUIActionType.Inspect);
        Assert.Contains(authorizer.Contexts, context => context.Action.Type == LiveUIActionType.SelectTab);
        Assert.Contains(authorizer.Contexts, context => context.Action.Type == LiveUIActionType.WaitForState);
        Assert.Contains(authorizer.Contexts, context => context.Action.Type == LiveUIActionType.Highlight);
        Assert.Contains(authorizer.Contexts, context => context.Action.Type == LiveUIActionType.ClearHighlight);
        Assert.Equal(0, runtime.Calls);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Client_inference_uses_only_a_screened_page_digest(bool allowed)
    {
        var broker = new ScreenedInteractionBroker(allowed);
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var infer = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_infer_plan"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await infer.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["intent"] = "Open tasks" })));

        Assert.Equal(1, broker.PageRequests);
        if (allowed)
        {
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(ScreenedInteractionBroker.PageJson)));
            Assert.Equal(expected, broker.PlanRequest!.ScreenedPageDigest);
            Assert.Equal("completed", result.GetProperty("outcome").GetString());
        }
        else
        {
            Assert.Null(broker.PlanRequest);
            Assert.Equal("page_unavailable", result.GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public async Task Client_inference_rate_limit_is_reported_without_a_plan_or_retry()
    {
        var broker = new RecordingInteractionBroker((_, request, kind) =>
            Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, kind,
                "{\"outcome\":\"rate_limited\",\"plan\":{\"actions\":[{\"type\":\"activate\"}]} }")));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var infer = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_infer_plan"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await infer.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["intent"] = "Open tasks" })));

        Assert.Equal("rate_limited", result.GetProperty("outcome").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("plan").ValueKind);
        Assert.Contains("do not retry", result.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(broker.Requests);
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
        Assert.Contains("SelectTab", executeFunction.Description, StringComparison.Ordinal);
        Assert.Contains("no verified postcondition", executeFunction.Description, StringComparison.Ordinal);
        var receipt = Assert.IsType<System.Text.Json.JsonElement>(await executeFunction.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" })));
        Assert.Equal("Completed", receipt.GetProperty("outcome").GetString());

        var request = Assert.Single(broker.Requests, item => item.Operation == "execute");
        Assert.Equal("todo.completed", request.TargetId);
        Assert.Equal("SelectTab", request.Action);

        var navigateFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_navigate"));
        Assert.Contains("call liveui_get_current_page again", navigateFunction.Description, StringComparison.Ordinal);
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
            new Dictionary<string, object?>
            {
                ["targetId"] = "todo.list",
                ["matchText"] = "Review the quarterly report",
                ["speechStartCharacter"] = 10,
                ["speechEndCharacter"] = 20
            }));
        var clearFunction = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_clear_highlight"));
        _ = await clearFunction.InvokeAsync(new AIFunctionArguments());

        Assert.Equal("todo.home", Assert.Single(broker.Requests, item => item.Operation == "navigate").RouteId);
        Assert.Equal("todo.list", Assert.Single(broker.Requests, item => item.Operation == "inspect").TargetId);
        var waitRequest = Assert.Single(broker.Requests, item => item.Operation == "await_state");
        Assert.Equal("todo.completed", waitRequest.TargetId);
        Assert.Equal("selected", waitRequest.StateName);
        var highlightRequest = Assert.Single(broker.Requests, item => item.Operation == "highlight");
        Assert.Equal("todo.list", highlightRequest.TargetId);
        Assert.Equal("Review the quarterly report", highlightRequest.Value);
        Assert.Equal(10, highlightRequest.SpeechStartCharacter);
        Assert.Equal(20, highlightRequest.SpeechEndCharacter);
        Assert.Single(broker.Requests, item => item.Operation == "clear_highlight");
        Assert.All(broker.Requests, item => Assert.True(Guid.TryParseExact(item.CorrelationId, "D", out _)));
    }

    [Fact]
    public async Task Missing_action_receipt_reports_uncertainty_and_does_not_imply_retry_is_safe()
    {
        var broker = new RecordingInteractionBroker((_, _, _) => Task.FromResult<ClientInteractionResponse?>(null));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" })));

        Assert.Equal("client_response_timeout", result.GetProperty("error").GetString());
        Assert.Equal("Uncertain", result.GetProperty("outcome").GetString());
        Assert.False(result.GetProperty("postconditionVerified").GetBoolean());
        Assert.False(result.GetProperty("retrySafe").GetBoolean());
        Assert.Contains("may already have happened", result.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uncertain_action_is_not_dispatched_again_in_the_same_agent_turn()
    {
        var broker = new RecordingInteractionBroker((_, _, _) => Task.FromResult<ClientInteractionResponse?>(null));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        { ["targetId"] = "todo.completed", ["action"] = "SelectTab" });

        var first = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(arguments));
        var retry = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(arguments));

        Assert.Equal("Uncertain", first.GetProperty("outcome").GetString());
        Assert.Equal("action_retry_unsafe", retry.GetProperty("error").GetString());
        Assert.False(retry.GetProperty("retrySafe").GetBoolean());
        Assert.Single(broker.Requests);
    }

    [Fact]
    public async Task Concurrent_action_on_same_target_waits_for_the_first_receipt()
    {
        var receipt = new TaskCompletionSource<ClientInteractionResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new RecordingInteractionBroker((_, _, _) => receipt.Task);
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        { ["targetId"] = "todo.completed", ["action"] = "SelectTab" });

        var first = execute.InvokeAsync(arguments).AsTask();
        var concurrent = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(arguments));

        Assert.Equal("action_in_progress", concurrent.GetProperty("error").GetString());
        var request = Assert.Single(broker.Requests);
        receipt.SetResult(new ClientInteractionResponse(request.CorrelationId, "action",
            "{\"outcome\":\"Completed\",\"postconditionVerified\":true}"));
        var completed = Assert.IsType<System.Text.Json.JsonElement>(await first);
        Assert.Equal("Completed", completed.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Observation_is_still_available_after_an_uncertain_action()
    {
        var broker = new RecordingInteractionBroker((_, _, _) => Task.FromResult<ClientInteractionResponse?>(null));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));
        var inspect = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_inspect"));

        await execute.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        { ["targetId"] = "todo.completed", ["action"] = "SelectTab" }));
        await inspect.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        { ["targetId"] = "todo.completed" }));

        Assert.Equal(new[] { "execute", "inspect" }, broker.Requests.Select(request => request.Operation));
    }

    [Theory]
    [InlineData("Completed", true, 2)]
    [InlineData("Denied", false, 2)]
    [InlineData("MissingTarget", false, 1)]
    [InlineData("Ambiguous", false, 1)]
    [InlineData("Failed", false, 1)]
    [InlineData("Conflict", false, 1)]
    public async Task Action_receipt_controls_whether_the_target_can_be_retried(string outcome, bool verified, int expectedRequests)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { outcome, postconditionVerified = verified });
        var broker = new RecordingInteractionBroker((_, request, kind) =>
            Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, kind, payload)));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        { ["targetId"] = "todo.completed", ["action"] = "SelectTab" });

        await execute.InvokeAsync(arguments);
        var retry = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(arguments));

        Assert.Equal(expectedRequests, broker.Requests.Count);
        if (expectedRequests == 1) Assert.Equal("action_retry_unsafe", retry.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(true, "client_response_mismatch")]
    [InlineData(false, "client_response_invalid")]
    public async Task Invalid_action_receipts_do_not_imply_an_action_was_safe_to_retry(bool mismatch, string error)
    {
        var broker = new RecordingInteractionBroker((_, request, kind) => Task.FromResult<ClientInteractionResponse?>(
            new(mismatch ? Guid.NewGuid().ToString("D") : request.CorrelationId, kind, mismatch ? "{}" : "not-json")));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" })));

        Assert.Equal(error, result.GetProperty("error").GetString());
        Assert.Equal("Uncertain", result.GetProperty("outcome").GetString());
        Assert.False(result.GetProperty("retrySafe").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"outcome\":\"Completed\",\"postconditionVerified\":false}")]
    [InlineData("{\"outcome\":\"Completed\",\"postconditionVerified\":\"true\"}")]
    [InlineData("{\"outcome\":\"MadeUp\",\"postconditionVerified\":true}")]
    [InlineData("{\"outcome\":\"Failed\",\"outcome\":\"Completed\",\"postconditionVerified\":true}")]
    [InlineData("{\"outcome\":\"Completed\",\"postconditionVerified\":false,\"postconditionVerified\":true}")]
    public async Task Structurally_invalid_action_receipt_is_uncertain(string payload)
    {
        var broker = new RecordingInteractionBroker((_, request, kind) =>
            Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, kind, payload)));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" })));

        Assert.Equal("client_response_invalid", result.GetProperty("error").GetString());
        Assert.Equal("Uncertain", result.GetProperty("outcome").GetString());
        Assert.False(result.GetProperty("retrySafe").GetBoolean());
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Uncertain")]
    public async Task Valid_noncompleted_receipt_keeps_its_original_outcome(string outcome)
    {
        var payload = $"{{\"outcome\":\"{outcome}\",\"postconditionVerified\":false}}";
        var broker = new RecordingInteractionBroker((_, request, kind) =>
            Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, kind, payload)));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var execute = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_execute"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await execute.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.completed", ["action"] = "SelectTab" })));

        Assert.Equal(outcome, result.GetProperty("outcome").GetString());
        Assert.False(result.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("x")]
    public async Task Client_highlight_tool_rejects_invalid_match_text(string matchText)
    {
        var broker = new RecordingInteractionBroker((_, request, resultKind) =>
            Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, resultKind, "{}")));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var highlight = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_highlight"));
        var value = matchText == "x" ? new string('x', 257) : matchText;

        var result = Assert.IsType<System.Text.Json.JsonElement>(await highlight.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["targetId"] = "todo.list", ["matchText"] = value })));

        Assert.Equal("invalid_request", result.GetProperty("error").GetString());
        Assert.Empty(broker.Requests);
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

    [Theory]
    [InlineData(null, 12)]
    [InlineData(12, null)]
    [InlineData(20, 20)]
    [InlineData(-1, 12)]
    [InlineData(0, 32_001)]
    public async Task Client_highlight_tool_rejects_invalid_speech_ranges(int? start, int? end)
    {
        var broker = new RecordingInteractionBroker((_, request, resultKind) =>
            Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, resultKind, "{}")));
        var tools = LiveUIClientAgentToolFactory.CreateTools(broker,
            new SessionId(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV")), TimeSpan.FromSeconds(1));
        var highlight = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "liveui_highlight"));

        var result = Assert.IsType<System.Text.Json.JsonElement>(await highlight.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["targetId"] = "todo.list",
                ["speechStartCharacter"] = start,
                ["speechEndCharacter"] = end
            })));

        Assert.Equal("invalid_request", result.GetProperty("error").GetString());
        Assert.Empty(broker.Requests);
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
    public async Task Highlight_with_speech_synchronization_remains_active_until_playback_finishes()
    {
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var options = new LiveUIOptions
        {
            Narration = new LiveUINarrationOptions { Enabled = true, SynchronizeWithSpeech = true }
        };
        var tools = new LiveUIAgentTools(new ContextAccessor(new LiveUIAgentContext("trusted-session", "todo.home")),
            new ManifestProvider(Manifest), authorizer, new LiveUIManifestValidator(),
            CreateOrchestrator(authorizer, runtime), Options.Create(options));

        var receipt = await tools.HighlightAsync("todo.search", durationMilliseconds: 1_500);

        Assert.Equal(LiveUIActionOutcome.Completed, receipt.Outcome);
        Assert.Equal(0, runtime.LastContext!.Action.Parameters.HighlightDurationMilliseconds);
    }

    [Fact]
    public async Task Highlight_without_speech_synchronization_keeps_requested_temporary_duration()
    {
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var tools = new LiveUIAgentTools(new ContextAccessor(new LiveUIAgentContext("trusted-session", "todo.home")),
            new ManifestProvider(Manifest), authorizer, new LiveUIManifestValidator(),
            CreateOrchestrator(authorizer, runtime), Options.Create(new LiveUIOptions()));

        var receipt = await tools.HighlightAsync("todo.search", durationMilliseconds: 1_500);

        Assert.Equal(LiveUIActionOutcome.Completed, receipt.Outcome);
        Assert.Equal(1_500, runtime.LastContext!.Action.Parameters.HighlightDurationMilliseconds);
    }

    [Fact]
    public async Task Highlight_text_targets_the_authorized_list_and_preserves_the_match_text()
    {
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var tools = CreateTools(authorizer, runtime);

        var receipt = await tools.HighlightTextAsync("todo.list", "Review the quarterly report");

        Assert.Equal(LiveUIActionOutcome.Completed, receipt.Outcome);
        Assert.Equal(LiveUIActionType.Highlight, runtime.LastContext!.Action.Type);
        Assert.Equal("todo.list", runtime.LastContext.Target!.Id);
        Assert.Equal("Review the quarterly report", runtime.LastContext.Action.Parameters.Value);
        Assert.Contains(authorizer.Contexts, context => context.Target?.Id == "todo.list" &&
            context.Action.Type == LiveUIActionType.Highlight);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("x")]
    public async Task Highlight_text_rejects_blank_or_oversized_match_without_authorization(string matchText)
    {
        var authorizer = new RecordingAuthorizer();
        var runtime = new RecordingRuntime();
        var tools = CreateTools(authorizer, runtime);
        var value = matchText == "x" ? new string('x', 257) : matchText;

        var receipt = await tools.HighlightTextAsync("todo.list", value);

        Assert.Equal(LiveUIActionOutcome.Denied, receipt.Outcome);
        Assert.Equal("invalid_highlight_match_text", receipt.Code);
        Assert.Empty(authorizer.Contexts);
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

    private sealed class MutableContextAccessor : ILiveUIAgentContextAccessor
    {
        public required LiveUIAgentContext Current { get; set; }
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(Current);
    }

    private sealed class NavigationRuntime(MutableContextAccessor accessor, bool verified) : ILiveUIRuntime
    {
        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (context.Action.Type == LiveUIActionType.Navigate)
            {
                accessor.Current = accessor.Current with { CurrentRouteId = "destination", CurrentPath = "/destination" };
                return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "route_ready", verified));
            }
            return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "verified", true));
        }
    }

    private sealed class ManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class MutableManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public LiveUIManifest Current { get; set; } = manifest;
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(Current);
    }

    private sealed class RecordingDiscovery(MutableManifestProvider provider) : ILiveUIRuntimeTargetDiscovery
    {
        public int Calls { get; private set; }
        public bool IsCurrentTarget(LiveUIAgentContext context, LiveUIManifest manifest, string targetId) => true;
        public ValueTask<bool> RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var target = new LiveUITarget(LiveUIRuntimeTargetIds.Prefix + "discovered", LiveUITargetKind.Button, "Save",
                new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Inspect });
            var replacement = manifest with { Routes = manifest.Routes.Select(item => item.Id == route.Id
                ? item with { Targets = item.Targets.Where(existing => existing.Id != target.Id).Append(target).ToArray() } : item).ToArray() };
            provider.Current = replacement with { ContentHash = LiveUIManifestHasher.ComputeContentHash(replacement) };
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FailedDiscovery : ILiveUIRuntimeTargetDiscovery
    {
        public ValueTask<bool> RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public bool IsCurrentTarget(LiveUIAgentContext context, LiveUIManifest manifest, string targetId) => false;
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

    private sealed class ScreenedInteractionBroker(bool allowed) : IClientInteractionBroker, IScreenedClientPageBroker
    {
        public const string PageJson = "{\"routeId\":\"home\",\"targets\":[]}";
        public int PageRequests { get; private set; }
        public ClientInteractionRequest? PlanRequest { get; private set; }

        public Task<ClientInteractionResponse?> RequestScreenedPageAsync(SessionId sessionId, ClientInteractionRequest request,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            PageRequests++;
            return Task.FromResult(allowed ? new ClientInteractionResponse(request.CorrelationId, "page", PageJson) : null);
        }

        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            PlanRequest = request;
            return Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, expectedResultKind,
                "{\"outcome\":\"completed\",\"plan\":{}}"));
        }
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
        public LiveUIExecutionContext? LastContext { get; private set; }
        public int Calls { get; private set; }
        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
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
