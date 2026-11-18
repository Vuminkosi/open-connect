using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using OpenConnect.Abstractions;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Components.Contracts;
using Xunit;

namespace OpenConnect.Blazor.Tests;

public sealed class MiniComposerSessionCreationTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Session_creation_authorization_failure_does_not_submit_task_or_lose_draft(int statusCode)
    {
        await using var context = new BunitContext();
        var (client, rendered, _) = await RenderComposerAsync(context, sessionCreationException:
            new OpenConnectClientException(statusCode, "unauthorized", "You don't have access to start a session."));

        await rendered.Find("input").InputAsync("Keep this draft");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();

        Assert.Empty(client.TaskRequests);
        Assert.Equal("Keep this draft", rendered.FindComponent<MudTextField<string>>().Instance.Value);
        Assert.Contains(statusCode == 403 ? "access to submit a message in this conversation." : "The assistant could not accept your message.", rendered.Markup);
        Assert.DoesNotContain("Message sent", rendered.Markup);
    }

    [Fact]
    public async Task Session_creation_failure_settles_until_explicit_send_retry_then_sends_once()
    {
        await using var context = new BunitContext();
        var (client, rendered, _) = await RenderComposerAsync(context, sessionCreationException:
            new HttpRequestException("session creation unavailable"));

        Assert.Equal(1, client.SessionCreationCount);
        Assert.Contains("Could not reach the assistant service.", rendered.Markup);

        rendered.Render();
        rendered.Render();
        Assert.Equal(1, client.SessionCreationCount);

        client.SessionCreationException = null;
        client.CompleteSessionCreationImmediately = true;
        await rendered.Find("input").InputAsync("Try again");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();

        Assert.Equal(2, client.SessionCreationCount);
        Assert.Equal(1, client.TaskSubmissionCount);
        var request = Assert.Single(client.TaskRequests);
        Assert.Equal("Try again", request.Message);
        Assert.Equal(client.SessionId.ToString(), request.SessionId);
    }

    [Fact]
    public async Task Task_not_found_is_not_retried_without_its_session_and_restores_draft()
    {
        await using var context = new BunitContext();
        var (client, rendered, _) = await RenderComposerAsync(context, taskException:
            new OpenConnectClientException(404, "task_not_found", "The task could not be found."));

        await rendered.Find("input").InputAsync("Keep this draft");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();

        Assert.Equal(1, client.SessionCreationCount);
        var request = Assert.Single(client.TaskRequests);
        Assert.Equal(client.SessionId.ToString(), request.SessionId);
        Assert.Equal(1, client.TaskSubmissionCount);
        Assert.Equal("Keep this draft", rendered.FindComponent<MudTextField<string>>().Instance.Value);
        Assert.Contains("This conversation is no longer available.", rendered.Markup);
        Assert.DoesNotContain("Message sent", rendered.Markup);
    }

    [Fact]
    public async Task Ambiguous_task_failure_reuses_identity_for_same_draft_and_rotates_after_edit()
    {
        await using var context = new BunitContext();
        var (client, rendered, session) = await RenderComposerAsync(context, loseFirstTaskResponseAfterAdmission: true);

        await rendered.Find("input").InputAsync("Run the report");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        var first = Assert.Single(client.TaskRequests);
        Assert.Equal("Run the report", rendered.FindComponent<MudTextField<string>>().Instance.Value);

        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        var second = client.TaskRequests[1];
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal(first.ClientTaskId, second.ClientTaskId);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Single(session.Messages, message => message.Role == "user" && message.Text == "Run the report");

        await rendered.InvokeAsync(session.CancelStreaming);

        client.LoseTaskResponsesAfterAdmission = true;
        await rendered.Find("input").InputAsync("Run the report");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        await rendered.Find("input").InputAsync("Run a different report");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        var edited = client.TaskRequests[3];
        Assert.NotEqual(second.IdempotencyKey, edited.IdempotencyKey);
        Assert.NotEqual(second.ClientTaskId, edited.ClientTaskId);
        Assert.Equal("Run a different report", edited.Message);
    }

    private static async Task<(DelayedSessionClient Client, IRenderedComponent<MiniComposer> Rendered, ComposerSession Session)> RenderComposerAsync(
        BunitContext context,
        Exception? sessionCreationException = null,
        Exception? taskException = null,
        bool loseFirstTaskResponseAfterAdmission = false)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
        var client = new DelayedSessionClient
        {
            SessionCreationException = sessionCreationException,
            CompleteSessionCreationImmediately = sessionCreationException is null,
            TaskException = taskException,
            LoseFirstTaskResponseAfterAdmission = loseFirstTaskResponseAfterAdmission
        };
        context.Services.AddSingleton<IOpenConnectClient>(client);
        var session = new ComposerSession();

        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<MudThemeProvider>(0);
                builder.CloseComponent();
                builder.OpenComponent<MudPopoverProvider>(1);
                builder.CloseComponent();
                builder.OpenComponent<MudDialogProvider>(2);
                builder.CloseComponent();
                builder.OpenComponent<MudSnackbarProvider>(3);
                builder.CloseComponent();
                builder.OpenComponent<MiniComposer>(4);
                builder.AddAttribute(5, nameof(MiniComposer.StartOpen), true);
                builder.AddAttribute(6, nameof(MiniComposer.AssistantName), "Ellai");
                builder.CloseComponent();
            }));
        await Task.Yield();
        return (client, rendered.FindComponent<MiniComposer>(), session);
    }

    [Fact]
    public async Task Sending_waits_for_the_registered_session_connection_before_admission()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
        var client = new RealtimeSessionClient { CompleteSessionCreationImmediately = true };
        context.Services.AddSingleton<IOpenConnectClient>(client);
        var session = new ComposerSession();
        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<MudPopoverProvider>(0);
                builder.CloseComponent();
                builder.OpenComponent<MiniComposer>(1);
                builder.AddAttribute(2, nameof(MiniComposer.StartOpen), true);
                builder.AddAttribute(3, nameof(MiniComposer.AssistantName), "Ellai");
                builder.CloseComponent();
            }));
        rendered.WaitForAssertion(() => Assert.Equal(client.SessionId.ToString(), session.SessionId));
        await rendered.Find("input").InputAsync("Show completed tasks.");
        var send = rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        rendered.WaitForAssertion(() => Assert.True(rendered.Find("button[aria-label='Stop reply']").HasAttribute("disabled") == false));
        Assert.Empty(client.TaskRequests);

        await rendered.InvokeAsync(() => session.RegisterConnection("registered-connection", client.SessionId.ToString()));
        await send.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(client.TaskRequests);
    }

    [Fact]
    public async Task Sending_while_initial_session_creation_is_pending_reuses_that_session()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
        var client = new DelayedSessionClient();
        context.Services.AddSingleton<IOpenConnectClient>(client);
        var session = new ComposerSession();

        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<MudThemeProvider>(0);
                builder.CloseComponent();
                builder.OpenComponent<MudPopoverProvider>(1);
                builder.CloseComponent();
                builder.OpenComponent<MudDialogProvider>(2);
                builder.CloseComponent();
                builder.OpenComponent<MudSnackbarProvider>(3);
                builder.CloseComponent();
                builder.OpenComponent<MiniComposer>(4);
                builder.AddAttribute(5, nameof(MiniComposer.StartOpen), true);
                builder.AddAttribute(6, nameof(MiniComposer.AssistantName), "Ellai");
                builder.CloseComponent();
            }));

        await client.SessionCreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await rendered.Find("input").InputAsync("Show completed tasks.");
        var send = rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        var composer = rendered.FindComponent<MiniComposer>();
        await composer.InvokeAsync(() => composer.Instance.SubmitFromJs());
        Assert.Equal(1, client.SessionCreationCount);
        Assert.Empty(client.TaskRequests);

        client.CompleteSessionCreation();
        await send.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, client.SessionCreationCount);
        var request = Assert.Single(client.TaskRequests);
        Assert.Equal(client.SessionId.ToString(), request.SessionId);
    }

    [Fact]
    public async Task Stopping_send_does_not_wait_for_the_shared_session_creation()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
        var client = new DelayedSessionClient();
        context.Services.AddSingleton<IOpenConnectClient>(client);
        var session = new ComposerSession();

        var rendered = context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(component => component.Value, session)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<MudThemeProvider>(0);
                builder.CloseComponent();
                builder.OpenComponent<MudPopoverProvider>(1);
                builder.CloseComponent();
                builder.OpenComponent<MudDialogProvider>(2);
                builder.CloseComponent();
                builder.OpenComponent<MudSnackbarProvider>(3);
                builder.CloseComponent();
                builder.OpenComponent<MiniComposer>(4);
                builder.AddAttribute(5, nameof(MiniComposer.StartOpen), true);
                builder.AddAttribute(6, nameof(MiniComposer.AssistantName), "Ellai");
                builder.CloseComponent();
            }));

        await client.SessionCreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await rendered.Find("input").InputAsync("Show completed tasks.");
        var send = rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        await rendered.Find("button[aria-label='Stop reply']").ClickAsync();
        await send.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(client.TaskRequests);
        Assert.Equal("Show completed tasks.", rendered.FindComponent<MudTextField<string>>().Instance.Value);
        Assert.Null(session.SessionId);
        client.CompleteSessionCreation();
        rendered.WaitForAssertion(() => Assert.Equal(client.SessionId.ToString(), session.SessionId));
        Assert.Equal(1, client.SessionCreationCount);
        Assert.Empty(client.TaskRequests);
        Assert.Equal("Show completed tasks.", rendered.FindComponent<MudTextField<string>>().Instance.Value);
    }

    [Fact]
    public async Task Stop_reply_cancels_the_admitted_gateway_task()
    {
        await using var context = new BunitContext();
        var (client, rendered, _) = await RenderComposerAsync(context);
        await rendered.Find("input").InputAsync("Show completed tasks.");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();

        await rendered.Find("button[aria-label='Stop reply']").ClickAsync();

        Assert.Equal(client.LastCreatedTaskId, Assert.Single(client.CancelledTaskIds));
    }

    [Fact]
    public async Task Stop_during_admission_cancels_the_task_once_its_identity_arrives()
    {
        await using var context = new BunitContext();
        var (client, rendered, _) = await RenderComposerAsync(context);
        client.HoldTaskAdmission = true;
        await rendered.Find("input").InputAsync("Show completed tasks.");
        var send = rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        await client.TaskAdmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await rendered.Find("button[aria-label='Stop reply']").ClickAsync();
        Assert.Empty(client.CancelledTaskIds);
        client.CompleteTaskAdmission();
        await send.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(client.LastCreatedTaskId, Assert.Single(client.CancelledTaskIds));
    }

    [Fact]
    public async Task Stop_failure_is_reported_instead_of_claiming_the_task_stopped()
    {
        await using var context = new BunitContext();
        var (client, rendered, session) = await RenderComposerAsync(context);
        client.CancelException = new HttpRequestException("private failure");
        await rendered.Find("input").InputAsync("Show completed tasks.");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();

        await rendered.Find("button[aria-label='Stop reply']").ClickAsync();

        Assert.Equal("Could not stop the reply. Please try again.", session.LastError);
        Assert.Equal(ComposerConnectionState.Streaming, session.Connection);
        Assert.DoesNotContain("private failure", rendered.Markup);
        client.CancelException = null;
        await rendered.Find("button[aria-label='Stop reply']").ClickAsync();
        Assert.Equal(2, client.CancelledTaskIds.Count);
    }

    [Fact]
    public async Task Late_stop_response_does_not_cancel_a_new_reply()
    {
        await using var context = new BunitContext();
        var (client, rendered, session) = await RenderComposerAsync(context);
        client.HoldCancellation = true;
        await rendered.Find("input").InputAsync("First request");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();
        var stop = rendered.Find("button[aria-label='Stop reply']").ClickAsync();
        await client.CancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await rendered.InvokeAsync(session.CancelStreaming);
        await rendered.Find("input").InputAsync("Second request");
        await rendered.Find("button[aria-label='Send to Ellai']").ClickAsync();

        client.CompleteCancellation();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ComposerConnectionState.Streaming, session.Connection);
        Assert.Equal(2, client.TaskRequests.Count);
    }

    private class DelayedSessionClient : IOpenConnectClient
    {
        private readonly TaskCompletionSource<SessionRecord> _sessionCreation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Ulid SessionId { get; } = Ulid.NewUlid();
        public int SessionCreationCount { get; private set; }
        public List<CreateTaskRequest> TaskRequests { get; } = [];
        public Exception? SessionCreationException { get; set; }
        public bool CompleteSessionCreationImmediately { get; set; }
        public Exception? TaskException { get; init; }
        public bool LoseFirstTaskResponseAfterAdmission { get; init; }
        public bool LoseTaskResponsesAfterAdmission { get; set; }
        public int TaskSubmissionCount { get; private set; }
        public TaskId? LastCreatedTaskId { get; private set; }
        public List<TaskId> CancelledTaskIds { get; } = [];
        public bool HoldTaskAdmission { get; set; }
        public Exception? CancelException { get; set; }
        public bool HoldCancellation { get; set; }
        public TaskCompletionSource CancellationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<TaskRecord> _cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskRecord? _cancelledRecord;
        public TaskCompletionSource TaskAdmissionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<TaskRecord> _taskAdmission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskRecord? _pendingRecord;
        public TaskCompletionSource SessionCreationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionRecord> CreateSessionAsync(CancellationToken cancellationToken = default)
        {
            SessionCreationCount++;
            SessionCreationStarted.TrySetResult();
            if (SessionCreationException is not null) return Task.FromException<SessionRecord>(SessionCreationException);
            return CompleteSessionCreationImmediately
                ? Task.FromResult(new SessionRecord(new SessionId(SessionId), new TenantId(Ulid.NewUlid()),
                    new PrincipalId(Ulid.NewUlid()), DateTimeOffset.UtcNow))
                : _sessionCreation.Task;
        }

        public void CompleteSessionCreation() => _sessionCreation.TrySetResult(new SessionRecord(
            new SessionId(SessionId), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), DateTimeOffset.UtcNow));

        public Task<TaskRecord> CreateMiniComposerTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default) =>
            CreateTaskAsync(request, cancellationToken);

        public Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
        {
            TaskSubmissionCount++;
            TaskRequests.Add(request);
            if ((LoseFirstTaskResponseAfterAdmission && TaskSubmissionCount == 1) || LoseTaskResponsesAfterAdmission)
                return Task.FromException<TaskRecord>(new HttpRequestException("Response was lost after admission."));
            if (TaskException is not null) return Task.FromException<TaskRecord>(TaskException);
            LastCreatedTaskId = new TaskId(Ulid.NewUlid());
            _pendingRecord = new TaskRecord(LastCreatedTaskId.Value, new TenantId(Ulid.NewUlid()),
                new PrincipalId(Ulid.NewUlid()), AgentRunState.Accepted);
            TaskAdmissionStarted.TrySetResult();
            return HoldTaskAdmission ? _taskAdmission.Task : Task.FromResult(_pendingRecord);
        }

        public void CompleteTaskAdmission() => _taskAdmission.TrySetResult(_pendingRecord!);

        public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
        {
            CancelledTaskIds.Add(taskId);
            if (CancelException is not null) return Task.FromException<TaskRecord>(CancelException);
            _cancelledRecord = new TaskRecord(taskId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Cancelled);
            CancellationStarted.TrySetResult();
            return HoldCancellation ? _cancellation.Task : Task.FromResult(_cancelledRecord);
        }
        public void CompleteCancellation() => _cancellation.TrySetResult(_cancelledRecord!);
        public Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CapabilityDescriptor>>([]);
        public async IAsyncEnumerable<EventRecord> SubscribeEventsAsync(SessionId sessionId, long afterSequence = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public async IAsyncEnumerable<CanonicalAgentEvent> ReplayEventsAsync(Guid sessionId, long afterSequence = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RealtimeSessionClient : DelayedSessionClient, IRealtimeOpenConnectClient
    {
        public async IAsyncEnumerable<EventRecord> SubscribeEventsWebSocketAsync(SessionId sessionId, long afterSequence = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopKeyInterceptorService : IKeyInterceptorService, IAsyncDisposable
    {
        public Task SubscribeAsync(IKeyInterceptorObserver observer, KeyInterceptorOptions options) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, IKeyDownObserver? keyDown = null, IKeyUpObserver? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, Action<Microsoft.AspNetCore.Components.Web.KeyboardEventArgs>? keyDown = null, Action<Microsoft.AspNetCore.Components.Web.KeyboardEventArgs>? keyUp = null) => Task.CompletedTask;
        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, Func<Microsoft.AspNetCore.Components.Web.KeyboardEventArgs, Task>? keyDown = null, Func<Microsoft.AspNetCore.Components.Web.KeyboardEventArgs, Task>? keyUp = null) => Task.CompletedTask;
        public Task UpdateKeyAsync(IKeyInterceptorObserver observer, KeyOptions options) => Task.CompletedTask;
        public Task UpdateKeyAsync(string elementId, KeyOptions options) => Task.CompletedTask;
        public Task UnsubscribeAsync(IKeyInterceptorObserver observer) => Task.CompletedTask;
        public Task UnsubscribeAsync(string elementId) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
