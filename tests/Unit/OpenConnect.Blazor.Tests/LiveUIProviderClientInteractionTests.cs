using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.Blazor.Tests;

public sealed class LiveUIProviderClientInteractionTests
{
    private const string Session = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Select_options_are_read_only_when_host_authorizes_the_control(bool inspectAllowed, bool selectAllowed)
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./_content/OpenConnect.LiveUI.Blazor/liveui.js");
        module.Setup<JsonElement?>("readSelectOptions", _ => true)
            .SetResult(JsonSerializer.Deserialize<JsonElement>("[{\"value\":\"2\",\"label\":\"Two\",\"selected\":false}]"));
        var client = new RecordingClient();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer>(_ => new SelectOptionsAuthorizer(inspectAllowed, selectAllowed));
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ => new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);
        var manifest = CreateManifest();
        var route = manifest.Routes[0];
        var select = new LiveUITarget("page.select", LiveUITargetKind.Select, "Page", new HashSet<LiveUICapability> { LiveUICapability.Select, LiveUICapability.Inspect });
        manifest = manifest with { Routes = [route with { Targets = route.Targets.Append(select).ToArray() }], ContentHash = "" };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var session = new ComposerSession();
        session.AttachSession(Session);
        session.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "get_select_options", "page.select") { ConnectionId = "test-connection" };
        await rendered.InvokeAsync(() => session.ApplyEvent(new EventRecord(new SessionId(Ulid.Parse(Session)), 1,
            ClientInteractionEventTypes.Request, JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        using var payload = JsonDocument.Parse(client.Response!.PayloadJson);
        Assert.Equal(inspectAllowed && selectAllowed, payload.RootElement.TryGetProperty("options", out _));
        Assert.Equal(inspectAllowed && selectAllowed ? 1 : 0, module.Invocations.Count(invocation => invocation.Identifier == "readSelectOptions"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Alert_content_is_read_only_when_host_authorizes_inspection(bool allowed)
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./_content/OpenConnect.LiveUI.Blazor/liveui.js");
        module.Setup<string?>("readApplicationAlert", _ => true).SetResult("The record changed while editing.");
        var client = new RecordingClient();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer>(_ => new ConditionalAuthorizer(allowed));
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ => new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<ILiveUIRuntimeTargetDiscovery>(provider => new StaticAlertDiscovery(provider.GetRequiredService<LiveUIManifestState>()));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);
        var session = new ComposerSession();
        session.AttachSession(Session);
        session.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), CreateManifest());
                builder.CloseComponent();
            }));
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "get_alert", "oc.runtime.alert.test") { ConnectionId = "test-connection" };
        await rendered.InvokeAsync(() => session.ApplyEvent(new EventRecord(new SessionId(Ulid.Parse(Session)), 1,
            ClientInteractionEventTypes.Request, JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        using var payload = JsonDocument.Parse(client.Response!.PayloadJson);
        Assert.Equal("page", client.Response.ResultKind);
        Assert.Equal(allowed, payload.RootElement.TryGetProperty("alertText", out _));
        Assert.Equal(allowed ? 1 : 0, module.Invocations.Count(invocation => invocation.Identifier == "readApplicationAlert"));
    }

    [Fact]
    public async Task Provider_rerender_preserves_discovery_but_session_or_provider_replacement_resets_it()
    {
        await using var context = new BunitContext();
        context.Services.AddOpenConnectLiveUIBlazor();
        var source = CreateManifest();
        var session = new ComposerSession();
        session.AttachSession(Session);
        var rendered = Render();
        var state = context.Services.GetRequiredService<LiveUIManifestState>();
        var discovered = source with { BuildId = "runtime-discovery", ContentHash = "" };
        discovered = discovered with { ContentHash = LiveUIManifestHasher.ComputeContentHash(discovered) };
        state.SetManifest(discovered);

        var liveProvider = rendered.FindComponent<OpenConnectLiveUIProvider>();
        await rendered.InvokeAsync(() => liveProvider.Render(parameters => parameters.Add(component => component.Manifest, source)));
        Assert.Same(discovered, await state.GetCurrentAsync());

        await rendered.InvokeAsync(() =>
        {
            session.AttachSession("01ARZ3NDEKTSV4RRFFQ69G5FB0");
        });
        Assert.Same(source, await state.GetCurrentAsync());

        state.SetManifest(discovered);
        rendered.Dispose();
        var replacement = Render();
        Assert.Same(source, await state.GetCurrentAsync());
        replacement.Dispose();

        IRenderedComponent<CascadingValue<ComposerSession>> Render() => context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), source);
                builder.CloseComponent();
            }));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Provider_dispatches_only_after_browser_confirmation(bool hostAllows, bool approved)
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./_content/OpenConnect.LiveUI.Blazor/liveui.js");
        module.Setup<bool>("confirmAction", _ => true).SetResult(approved);
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer>(_ => new ConditionalAuthorizer(hostAllows));
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ => new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);
        var manifest = CreateManifest();
        var route = manifest.Routes[0];
        var targets = route.Targets.Select(target => target.Id == "todo.completed" ? target with { Kind = LiveUITargetKind.Button, Label = "Delete task", RequiresConfirmation = true } : target).ToArray();
        manifest = manifest with { Routes = [route with { Targets = targets }] };
        manifest = manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
        var session = new ComposerSession();
        session.AttachSession(Session);
        session.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "execute", "todo.completed", "Click") { ConnectionId = "test-connection" };
        await rendered.InvokeAsync(() => session.ApplyEvent(new EventRecord(new SessionId(Ulid.Parse(Session)), 1,
            ClientInteractionEventTypes.Request, JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        using var receipt = JsonDocument.Parse(client.Response!.PayloadJson);
        Assert.Equal(hostAllows && approved ? "Completed" : "Denied", receipt.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(hostAllows && approved ? 1 : 0, runtime.Calls);
        Assert.Equal(hostAllows ? 1 : 0, module.Invocations.Count(invocation => invocation.Identifier == "confirmAction"));
    }

    [Fact]
    public async Task Provider_recreation_in_same_scope_does_not_replay_a_request()
    {
        await using var context = new BunitContext();
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ => new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);
        var session = new ComposerSession();
        session.AttachSession(Session);
        session.RegisterConnection("test-connection", Session);
        var first = Render();
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "execute", "todo.completed", "SelectTab") { ConnectionId = "test-connection" };
        await first.InvokeAsync(() => Send(1));
        first.WaitForAssertion(() => Assert.Equal(1, runtime.Calls));
        first.Dispose();
        var second = Render();
        await second.InvokeAsync(() => Send(2));
        Assert.Equal(1, runtime.Calls);

        IRenderedComponent<CascadingValue<ComposerSession>> Render() => context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), CreateManifest());
                builder.CloseComponent();
            }));
        void Send(long sequence) => session.ApplyEvent(new EventRecord(new SessionId(Ulid.Parse(Session)), sequence,
            ClientInteractionEventTypes.Request, JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions())));
    }

    [Fact]
    public async Task Provider_does_not_execute_a_nonconsecutive_replayed_request()
    {
        await using var context = new BunitContext();
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ => new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);
        var session = new ComposerSession();
        session.AttachSession(Session);
        session.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), CreateManifest());
                builder.CloseComponent();
            }));
        var first = new ClientInteractionRequest("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "execute", "todo.completed", "SelectTab") { ConnectionId = "test-connection" };
        var second = first with { CorrelationId = Guid.NewGuid().ToString("D") };
        await Send(first, 1);
        rendered.WaitForAssertion(() => Assert.Equal(1, runtime.Calls));
        await Send(second, 2);
        rendered.WaitForAssertion(() => Assert.Equal(2, runtime.Calls));
        await Send(first, 3);
        Assert.Equal(2, runtime.Calls);
        await Send(first with { CorrelationId = first.CorrelationId.ToUpperInvariant() }, 4);
        Assert.Equal(2, runtime.Calls);

        Task Send(ClientInteractionRequest request, long sequence) => rendered.InvokeAsync(() => session.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), sequence, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions()))));
    }

    public static TheoryData<ClientInteractionRequest, string> Requests => new()
    {
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000001", "navigate", RouteId: "todo.home"), "Navigate" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000002", "inspect", TargetId: "todo.list"), "Inspect" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000003", "execute", TargetId: "todo.completed", Action: "SelectTab"), "SelectTab" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000010", "execute", TargetId: "todo.completed", Action: "Click", StateName: "selected", StateValue: "true"), "Click" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000004", "await_state", TargetId: "todo.completed", StateName: "selected"), "WaitForState" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000005", "highlight", TargetId: "todo.list", HighlightDurationMilliseconds: 1_250), "Highlight" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000009", "highlight", TargetId: "todo.list", Value: "Review the quarterly report"), "Highlight" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000010", "highlight", TargetId: "todo.list", Value: " "), "invalid" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000011", "highlight", TargetId: "todo.list", Value: new string('x', 257)), "invalid" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000006", "clear_highlight"), "ClearHighlight" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000007", "await_state", TargetId: "todo.region", StateName: "textContains", StateValue: "No tasks to display"), "WaitForState" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000008", "highlight", TargetId: "todo.list")
            { SpeechStartCharacter = 1 }, "invalid" }
    };

    [Fact]
    public async Task Server_dispatched_inference_uses_current_authorized_manifest_and_returns_plan()
    {
        await using var context = new BunitContext();
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var router = new RecordingInferenceRouter();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIInferenceRouter>(_ => router);
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => new RecordingRuntime());
        context.Services.AddScoped<IOpenConnectClient>(_ => client);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ =>
            new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));
        var interaction = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "infer_plan", Value: "Show completed tasks");

        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(interaction with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));

        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        Assert.Equal(interaction.CorrelationId, client.Response!.CorrelationId);
        Assert.Equal("plan", client.Response.ResultKind);
        Assert.Equal(Session, router.Request!.SessionId);
        Assert.Equal(interaction.CorrelationId, router.Request.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(router.Request.ExecutionId));
        Assert.Equal("todo.home", router.Request.RouteId);
        Assert.Contains(router.Request.Capabilities, item => item.TargetId == "todo.completed" && item.Actions.Contains("activate"));
        using var payload = JsonDocument.Parse(client.Response.PayloadJson);
        Assert.Equal("completed", payload.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("todo.completed", payload.RootElement.GetProperty("plan").GetProperty("actions")[0].GetProperty("targetId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Screened_page_digest_binds_runtime_targets_before_inference(bool pageChanged)
    {
        await using var context = new BunitContext();
        var client = new RecordingClient();
        var router = new RecordingInferenceRouter();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIInferenceRouter>(_ => router);
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => new RecordingRuntime());
        context.Services.AddScoped<IOpenConnectClient>(_ => client);
        context.Services.AddScoped<StaticRuntimeDiscovery>();
        context.Services.AddScoped<ILiveUIRuntimeTargetDiscovery>(provider => provider.GetRequiredService<StaticRuntimeDiscovery>());
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ =>
            new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home") { CurrentPath = "/" }));

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), CreateManifest());
                builder.CloseComponent();
            }));
        var pageRequest = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "get_current_page")
        { ConnectionId = "test-connection" };
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(pageRequest, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.Equal(pageRequest.CorrelationId, client.Response?.CorrelationId));
        var pageJson = client.Response!.PayloadJson;
        Assert.Contains("oc.runtime.discovered", pageJson, StringComparison.Ordinal);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pageJson)));
        if (pageChanged)
            context.Services.GetRequiredService<StaticRuntimeDiscovery>().Label = "Different control";

        var inference = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "infer_plan", Value: "Open the control")
        { ConnectionId = "test-connection", ScreenedPageDigest = digest };
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 2, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(inference, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.Equal(inference.CorrelationId, client.Response?.CorrelationId));

        if (pageChanged)
        {
            Assert.Null(router.Request);
            using var response = JsonDocument.Parse(client.Response!.PayloadJson);
            Assert.Equal("page_unavailable", response.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("screened_page_changed", response.RootElement.GetProperty("code").GetString());
        }
        else
        {
            Assert.NotNull(router.Request);
            Assert.Contains(router.Request.Capabilities, item => item.TargetId == "oc.runtime.discovered");
        }
    }

    [Fact]
    public async Task Provider_cancels_the_matching_browser_action_and_suppresses_a_stale_receipt()
    {
        await using var context = new BunitContext();
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var runtime = new CancellationAwareRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ =>
            new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", "todo.list");

        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(request with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 2, ClientInteractionEventTypes.Cancellation,
            JsonSerializer.Serialize(new ClientInteractionCancellation(request.CorrelationId), OpenConnectHttpJson.CreateOptions()))));

        await runtime.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.HighlightCleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(25);
        Assert.Null(client.Response);
    }

    [Fact]
    public async Task Provider_binds_browser_action_to_the_originating_composer_session()
    {
        await using var context = new BunitContext();
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<IOpenConnectClient>(_ => client);

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", "todo.list");

        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(request with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));

        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        using var receipt = JsonDocument.Parse(client.Response!.PayloadJson);
        Assert.Equal(Session, receipt.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(Session, runtime.LastSessionId);
    }

    [Fact]
    public async Task Provider_cancels_interaction_when_composer_session_changes()
    {
        await using var context = new BunitContext();
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var runtime = new CancellationAwareRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<IOpenConnectClient>(_ => client);

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", "todo.list");

        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(request with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await rendered.InvokeAsync(() => composerSession.AttachSession("01ARZ3NDEKTSV4RRFFQ69G5FAW"));

        await runtime.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.HighlightCleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(client.Response);
    }

    [Fact]
    public async Task Provider_clears_highlight_when_cancellation_arrives_after_the_action_receipt()
    {
        await using var context = new BunitContext();
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ =>
            new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", "todo.list");

        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(request with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        await Task.Delay(25);
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 2, ClientInteractionEventTypes.Cancellation,
            JsonSerializer.Serialize(new ClientInteractionCancellation(request.CorrelationId), OpenConnectHttpJson.CreateOptions()))));

        await runtime.HighlightCleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runtime.ClearHighlightCount);
    }

    [Fact]
    public async Task Provider_clears_highlight_from_actual_composer_response_completion_without_host_callback()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ =>
            new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.AddAttribute(2, nameof(OpenConnectLiveUIProvider.ChildContent), (RenderFragment)(childBuilder =>
                {
                    childBuilder.OpenComponent<MudThemeProvider>(0);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MudPopoverProvider>(1);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MudDialogProvider>(2);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MudSnackbarProvider>(3);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MiniComposer>(4);
                    childBuilder.AddAttribute(5, nameof(MiniComposer.StartOpen), true);
                    childBuilder.CloseComponent();
                }));
                builder.CloseComponent();
            }));

        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", "todo.list");
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(request with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        Assert.Equal("Highlight", runtime.LastAction);

        await rendered.InvokeAsync(() => rendered.Find(".oc-bubble__speech-action .oc-speech-item").Click());
        await runtime.HighlightCleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runtime.ClearHighlightCount);
        composerSession.BeginSend("Explain the completed tasks.");
        await rendered.InvokeAsync(() =>
        {
            composerSession.ApplyEvent(new EventRecord(new SessionId(Ulid.Parse(Session)), 2, "message.delta", "The list shows completed tasks."));
            composerSession.ApplyEvent(new EventRecord(new SessionId(Ulid.Parse(Session)), 3, "message.completed", string.Empty));
        });

        rendered.WaitForAssertion(() => Assert.Equal(2, runtime.ClearHighlightCount));
    }

    [Fact]
    public async Task Provider_switches_authorized_targets_at_their_audio_segment_character_ranges()
    {
        var synchronizationOffsets = new List<double>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "OpenConnect.LiveUI")
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "openconnect.liveui.speech.highlight.offset")
                synchronizationOffsets.Add(measurement);
        });
        meterListener.Start();

        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor(options =>
        {
            options.Narration.Enabled = true;
            options.Narration.SynchronizeWithSpeech = true;
        });
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ =>
            new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.AddAttribute(2, nameof(OpenConnectLiveUIProvider.ChildContent), (RenderFragment)(childBuilder =>
                {
                    childBuilder.OpenComponent<MudThemeProvider>(0);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MudPopoverProvider>(1);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MudDialogProvider>(2);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MudSnackbarProvider>(3);
                    childBuilder.CloseComponent();
                    childBuilder.OpenComponent<MiniComposer>(4);
                    childBuilder.AddAttribute(5, nameof(MiniComposer.StartOpen), true);
                    childBuilder.CloseComponent();
                }));
                builder.CloseComponent();
            }));

        await rendered.InvokeAsync(() => rendered.Find(".oc-bubble__speech-action .oc-speech-item").Click());
        var responseText = "Tasks completed: report and cleanup.";
        var firstSegmentEnd = responseText.IndexOf("report", StringComparison.Ordinal);
        await SendHighlightCueAsync("todo.list", 0, firstSegmentEnd, 1);
        await SendHighlightCueAsync("todo.region", 0, firstSegmentEnd, 2);
        await SendHighlightCueWithMatchAsync("todo.list", "report", firstSegmentEnd + 2, responseText.Length, 3);

        composerSession.BeginSend("Explain the completed tasks.");
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 4, "message.delta", responseText)));
        var composer = rendered.FindComponent<MiniComposer>().Instance;

        var firstEpoch = composerSession.BeginSpeechPlayback();
        await rendered.InvokeAsync(() => composer.OnSpeechPlaybackStarted(firstEpoch, 0, firstSegmentEnd, 1));
        Assert.Equal(new[] { "todo.list", "todo.region" }, runtime.ActiveHighlightTargets.Order(StringComparer.Ordinal));
        await rendered.InvokeAsync(() => composer.OnSpeechPlaybackSynchronizationMeasured(
            firstEpoch, 0, firstSegmentEnd, 42.5));
        Assert.Equal(42.5, Assert.Single(synchronizationOffsets));

        Assert.True(composerSession.EndSpeechPlayback(firstEpoch));
        var clearCountBeforeUnmatchedSegment = runtime.ClearHighlightCount;
        var unmatchedEpoch = composerSession.BeginSpeechPlayback();
        await rendered.InvokeAsync(() => composer.OnSpeechPlaybackStarted(unmatchedEpoch, firstSegmentEnd, firstSegmentEnd + 2, 1));
        Assert.Equal("ClearHighlight", runtime.LastAction);
        Assert.Equal(clearCountBeforeUnmatchedSegment + 1, runtime.ClearHighlightCount);
        await rendered.InvokeAsync(() => composer.OnSpeechPlaybackSynchronizationMeasured(
            unmatchedEpoch, firstSegmentEnd, firstSegmentEnd + 2, 18));
        Assert.Single(synchronizationOffsets);

        Assert.True(composerSession.EndSpeechPlayback(unmatchedEpoch));
        var secondEpoch = composerSession.BeginSpeechPlayback();
        await rendered.InvokeAsync(() => composer.OnSpeechPlaybackStarted(secondEpoch, firstSegmentEnd + 2, responseText.Length, 1));
        Assert.Equal("todo.list", runtime.LastTargetId);
        Assert.Equal("report", runtime.LastParameters!.Value);
        await rendered.InvokeAsync(() => composer.OnSpeechPlaybackSynchronizationMeasured(
            secondEpoch, firstSegmentEnd + 2, responseText.Length, 71.25));
        Assert.Equal(new[] { 42.5, 71.25 }, synchronizationOffsets);

        Assert.True(composerSession.EndSpeechPlayback(secondEpoch));

        await SendHighlightCueAsync("todo.completed-highlight", firstSegmentEnd + 2, responseText.Length, 5);
        runtime.FailedHighlightTargets.Add("todo.completed-highlight");
        var clearCountBeforePartialCue = runtime.ClearHighlightCount;
        var partialCueEpoch = composerSession.BeginSpeechPlayback();
        await rendered.InvokeAsync(() => composer.OnSpeechPlaybackStarted(
            partialCueEpoch, firstSegmentEnd + 2, responseText.Length, 1));
        Assert.Empty(runtime.ActiveHighlightTargets);
        Assert.Equal(clearCountBeforePartialCue + 2, runtime.ClearHighlightCount);

        Assert.True(composerSession.EndSpeechPlayback(partialCueEpoch));
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 5, "message.completed", string.Empty)));
        rendered.WaitForAssertion(() => Assert.Equal("ClearHighlight", runtime.LastAction));

        async Task SendHighlightCueAsync(string targetId, int startCharacter, int endCharacter, long sequence) =>
            await SendHighlightCueWithMatchAsync(targetId, null, startCharacter, endCharacter, sequence);

        async Task SendHighlightCueWithMatchAsync(string targetId, string? matchText, int startCharacter, int endCharacter, long sequence)
        {
            var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", targetId,
                Value: matchText,
                HighlightDurationMilliseconds: 2_000)
            {
                SpeechStartCharacter = startCharacter,
                SpeechEndCharacter = endCharacter
            };
            await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
                new SessionId(Ulid.Parse(Session)), sequence, ClientInteractionEventTypes.Request,
                JsonSerializer.Serialize(request with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));
            rendered.WaitForAssertion(() => Assert.Equal(request.CorrelationId, client.Response?.CorrelationId));
            Assert.Equal(0, runtime.LastParameters!.HighlightDurationMilliseconds);
        }
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Provider_executes_correlated_operations_and_posts_verified_receipts(
        ClientInteractionRequest request, string expectedAction)
    {
        await using var context = new BunitContext();
        var manifest = CreateManifest();
        var client = new RecordingClient();
        var runtime = new RecordingRuntime();
        context.Services.AddOpenConnectLiveUIBlazor();
        context.Services.AddScoped<ILiveUIActionAuthorizer, AllowAuthorizer>();
        context.Services.AddScoped<ILiveUIRuntime>(_ => runtime);
        context.Services.AddScoped<ILiveUIAgentContextAccessor>(_ =>
            new FixedContextAccessor(new LiveUIAgentContext(Session, "todo.home")));
        context.Services.AddScoped<IOpenConnectClient>(_ => client);

        var composerSession = new ComposerSession();
        composerSession.AttachSession(Session);
        composerSession.RegisterConnection("test-connection", Session);
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, composerSession)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<OpenConnectLiveUIProvider>(0);
                builder.AddAttribute(1, nameof(OpenConnectLiveUIProvider.Manifest), manifest);
                builder.CloseComponent();
            }));

        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 1, ClientInteractionEventTypes.Request,
            JsonSerializer.Serialize(request with { ConnectionId = "test-connection" }, OpenConnectHttpJson.CreateOptions()))));

        rendered.WaitForAssertion(() =>
        {
            Assert.NotNull(client.Response);
            Assert.Equal(Session, client.SessionId!.Value.Value.ToString());
        });

        Assert.Equal(request.CorrelationId, client.Response!.CorrelationId);
        Assert.Equal("action", client.Response.ResultKind);
        using var receipt = JsonDocument.Parse(client.Response.PayloadJson);
        if (expectedAction == "invalid")
        {
            Assert.Null(runtime.LastAction);
            Assert.Equal("client_interaction_failed", receipt.RootElement.GetProperty("code").GetString());
            return;
        }

        Assert.Equal(expectedAction, runtime.LastAction);
        if (request.Operation == "highlight" && request.Value is not null)
            Assert.Equal(request.Value, runtime.LastParameters!.Value);
        if (request.Operation == "highlight")
            Assert.Equal(request.HighlightDurationMilliseconds, runtime.LastParameters!.HighlightDurationMilliseconds);
        if (request.Operation == "execute" && request.StateName is not null)
        {
            Assert.Equal(request.StateName, runtime.LastParameters!.StateName);
            Assert.Equal(request.StateValue, runtime.LastParameters.StateValue);
        }
        Assert.Equal("Completed", receipt.RootElement.GetProperty("outcome").GetString());
        Assert.True(receipt.RootElement.GetProperty("postconditionVerified").GetBoolean());
    }

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-1", string.Empty,
        [
            new LiveUIRoute("todo.home", "/", "Todo",
            [
                new LiveUITarget("todo.completed", LiveUITargetKind.Tab, "Completed",
                    new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Filter, LiveUICapability.WaitForState }),
                new LiveUITarget("todo.completed-highlight", LiveUITargetKind.Region, "Completed task cue",
                    new HashSet<LiveUICapability> { LiveUICapability.Highlight }),
                new LiveUITarget("todo.list", LiveUITargetKind.List, "Task list",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight }),
                new LiveUITarget("todo.region", LiveUITargetKind.Region, "todo-list",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.WaitForState })
            ])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private sealed class RecordingInferenceRouter : ILiveUIInferenceRouter
    {
        public LiveUIInferenceRequest? Request { get; private set; }

        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser,
                new LiveUIActionPlan(request.SchemaVersion, [new("activate", "todo.completed")]), "completed"));
        }
    }

    private sealed class StaticAlertDiscovery(LiveUIManifestState state) : ILiveUIRuntimeTargetDiscovery
    {
        public ValueTask<bool> RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
            CancellationToken cancellationToken = default)
        {
            var alert = new LiveUITarget("oc.runtime.alert.test", LiveUITargetKind.Region, "Application alert",
                new HashSet<LiveUICapability> { LiveUICapability.Inspect }, Role: "alert");
            var replacement = manifest with
            {
                Routes = manifest.Routes.Select(item => item.Id == route.Id
                ? item with { Targets = item.Targets.Where(target => target.Id != alert.Id).Append(alert).ToArray() } : item).ToArray(),
                ContentHash = ""
            };
            state.SetManifest(replacement with { ContentHash = LiveUIManifestHasher.ComputeContentHash(replacement) });
            return ValueTask.FromResult(true);
        }
        public bool IsCurrentTarget(LiveUIAgentContext context, LiveUIManifest manifest, string targetId) => targetId == "oc.runtime.alert.test";
    }

    private sealed class StaticRuntimeDiscovery(LiveUIManifestState state) : ILiveUIRuntimeTargetDiscovery
    {
        public string Label { get; set; } = "Open control";

        public ValueTask<bool> RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
            CancellationToken cancellationToken = default)
        {
            var discovered = new LiveUITarget("oc.runtime.discovered", LiveUITargetKind.Button, Label,
                new HashSet<LiveUICapability> { LiveUICapability.Activate }, RequiresConfirmation: true);
            var targets = route.Targets.Where(item => item.Id != discovered.Id).Append(discovered).ToArray();
            var replacement = manifest with { Routes = manifest.Routes.Select(item => item.Id == route.Id ? item with { Targets = targets } : item).ToArray(), ContentHash = "" };
            state.SetManifest(replacement with { ContentHash = LiveUIManifestHasher.ComputeContentHash(replacement) });
            return ValueTask.FromResult(true);
        }

        public bool IsCurrentTarget(LiveUIAgentContext context, LiveUIManifest manifest, string targetId) =>
            targetId == "oc.runtime.discovered";
    }

    private sealed class AllowAuthorizer : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "allowed"));
    }

    private sealed class SelectOptionsAuthorizer(bool inspect, bool select) : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(context.Target is null || context.Action.Type switch
            {
                LiveUIActionType.Inspect => inspect,
                LiveUIActionType.Select => select,
                _ => false
            }, "policy"));
    }

    private sealed class ConditionalAuthorizer(bool allowed) : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new LiveUIAuthorizationDecision(allowed, "policy"));
    }

    private sealed class FixedContextAccessor(LiveUIAgentContext context) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(context);
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
        public int Calls { get; private set; }
        public string? LastAction { get; private set; }
        public string? LastTargetId { get; private set; }
        public string? LastSessionId { get; private set; }
        public LiveUIActionParameters? LastParameters { get; private set; }
        public int ClearHighlightCount { get; private set; }
        public HashSet<string> ActiveHighlightTargets { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailedHighlightTargets { get; } = new(StringComparer.Ordinal);
        public TaskCompletionSource<bool> HighlightCleared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastAction = context.Action.Type.ToString();
            LastTargetId = context.Target?.Id;
            LastSessionId = context.Action.SessionId;
            LastParameters = context.Action.Parameters;
            if (context.Action.Type == LiveUIActionType.ClearHighlight)
            {
                ClearHighlightCount++;
                ActiveHighlightTargets.Clear();
                HighlightCleared.TrySetResult(true);
            }
            else if (context.Action.Type == LiveUIActionType.Highlight && context.Target is { } target)
            {
                if (FailedHighlightTargets.Contains(target.Id))
                    return ValueTask.FromResult(new LiveUIRuntimeResult(
                        LiveUIActionOutcome.Failed, "fixture_highlight_failed", false));
                ActiveHighlightTargets.Add(target.Id);
            }
            return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "state_verified", true, "state_verified"));
        }
    }

    private sealed class CancellationAwareRuntime : ILiveUIRuntime
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> HighlightCleared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.Action.Type == LiveUIActionType.ClearHighlight)
            {
                HighlightCleared.TrySetResult(true);
                return new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "highlight_cleared", true, "cleared");
            }

            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "unexpected_completion", true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult(true);
                throw;
            }
        }
    }

    private sealed class RecordingClient : IOpenConnectClient
    {
        public SessionId? SessionId { get; private set; }
        public ClientInteractionResponse? Response { get; private set; }

        public Task PostClientInteractionResponseAsync(SessionId sessionId, ClientInteractionResponse response,
            CancellationToken cancellationToken = default)
        {
            SessionId = sessionId;
            Response = response;
            return Task.CompletedTask;
        }

        public Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<EventRecord> SubscribeEventsAsync(SessionId sessionId, long afterSequence = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<CanonicalAgentEvent> ReplayEventsAsync(Guid sessionId, long afterSequence = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
