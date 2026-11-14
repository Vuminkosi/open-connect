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
        Assert.Equal(1, client.SessionCreationCount);

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
        client.CompleteSessionCreation();
        rendered.WaitForAssertion(() => Assert.Equal(client.SessionId.ToString(), session.SessionId));
        Assert.Equal(1, client.SessionCreationCount);
    }

    private sealed class DelayedSessionClient : IOpenConnectClient
    {
        private readonly TaskCompletionSource<SessionRecord> _sessionCreation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Ulid SessionId { get; } = Ulid.NewUlid();
        public int SessionCreationCount { get; private set; }
        public List<CreateTaskRequest> TaskRequests { get; } = [];
        public TaskCompletionSource SessionCreationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionRecord> CreateSessionAsync(CancellationToken cancellationToken = default)
        {
            SessionCreationCount++;
            SessionCreationStarted.TrySetResult();
            return _sessionCreation.Task;
        }

        public void CompleteSessionCreation() => _sessionCreation.TrySetResult(new SessionRecord(
            new SessionId(SessionId), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), DateTimeOffset.UtcNow));

        public Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
        {
            TaskRequests.Add(request);
            return Task.FromResult(new TaskRecord(new TaskId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()),
                new PrincipalId(Ulid.NewUlid()), AgentRunState.Accepted));
        }

        public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
