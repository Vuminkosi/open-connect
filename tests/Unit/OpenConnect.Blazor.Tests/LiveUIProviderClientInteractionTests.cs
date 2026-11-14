using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Blazor;
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
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000006", "clear_highlight"), "ClearHighlight" },
        { new ClientInteractionRequest("00000000-0000-0000-0000-000000000007", "await_state", TargetId: "todo.region", StateName: "textContains", StateValue: "No tasks to display"), "WaitForState" }
    };

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
            JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions()))));
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
            JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions()))));
        rendered.WaitForAssertion(() => Assert.NotNull(client.Response));
        await Task.Delay(25);
        await rendered.InvokeAsync(() => composerSession.ApplyEvent(new EventRecord(
            new SessionId(Ulid.Parse(Session)), 2, ClientInteractionEventTypes.Cancellation,
            JsonSerializer.Serialize(new ClientInteractionCancellation(request.CorrelationId), OpenConnectHttpJson.CreateOptions()))));

        await runtime.HighlightCleared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, runtime.ClearHighlightCount);
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
            JsonSerializer.Serialize(request, OpenConnectHttpJson.CreateOptions()))));

        rendered.WaitForAssertion(() =>
        {
            Assert.NotNull(client.Response);
            Assert.Equal(Session, client.SessionId!.Value.Value.ToString());
        });

        Assert.Equal(request.CorrelationId, client.Response!.CorrelationId);
        Assert.Equal("action", client.Response.ResultKind);
        using var receipt = JsonDocument.Parse(client.Response.PayloadJson);
        Assert.Equal(expectedAction, runtime.LastAction);
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
                new LiveUITarget("todo.list", LiveUITargetKind.List, "Task list",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight }),
                new LiveUITarget("todo.region", LiveUITargetKind.Region, "todo-list",
                    new HashSet<LiveUICapability> { LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.WaitForState })
            ])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
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
        public LiveUIActionParameters? LastParameters { get; private set; }
        public int ClearHighlightCount { get; private set; }
        public TaskCompletionSource<bool> HighlightCleared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            LastAction = context.Action.Type.ToString();
            LastParameters = context.Action.Parameters;
            if (context.Action.Type == LiveUIActionType.ClearHighlight)
            {
                ClearHighlightCount++;
                HighlightCleared.TrySetResult(true);
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
