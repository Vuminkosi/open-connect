using System.Diagnostics.Metrics;
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

    public static TheoryData<ClientInteractionRequest, string> Requests => new()
    {
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000001", "navigate", RouteId: "todo.home"), "Navigate" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000002", "inspect", TargetId: "todo.list"), "Inspect" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000003", "execute", TargetId: "todo.completed", Action: "SelectTab"), "SelectTab" },
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
        using var context = new BunitContext();
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

    [Fact]
    public async Task Provider_cancels_the_matching_browser_action_and_suppresses_a_stale_receipt()
    {
        using var context = new BunitContext();
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
        using var context = new BunitContext();
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
        using var context = new BunitContext();
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
        using var context = new BunitContext();
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
        using var context = new BunitContext();
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

    private sealed class AllowAuthorizer : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "allowed"));
    }

    private sealed class FixedContextAccessor(LiveUIAgentContext context) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(context);
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
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
