using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using OpenConnect.Abstractions;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Components.Contracts;
using System.Runtime.CompilerServices;
using Xunit;

namespace OpenConnect.Blazor.Tests;

/// <summary>
/// Rendered-component evidence for <see cref="OpenConnectProvider"/> (CF-AT-027):
/// session/registry cascade, child content, no duplicate Mud providers, clean disposal.
/// The host must supply MudThemeProvider, MudPopoverProvider, MudDialogProvider and
/// MudSnackbarProvider (see the MiniComposer.razor header and
/// OpenConnectBlazorServiceCollectionExtensions, which never registers them);
/// these tests supply Mud services only.
/// </summary>
public sealed class OpenConnectProviderRenderTests : BunitContext
{
    public OpenConnectProviderRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        // No-op keyboard-interception stub (see MiniComposerRenderTests): keeps bUnit sync teardown clean.
        Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        Services.AddSingleton<IOpenConnectClient>(new FakeOpenConnectClient());
        Services.AddSingleton(new OpenConnectComponentRegistry());
        Services.AddSingleton(NewSession());
    }

    [Fact]
    public void Provider_cascades_session_and_renders_child_content()
    {
        var session = Services.GetRequiredService<ComposerSession>();
        session.BeginSend("hello from provider");

        var cut = RenderProvider(MiniComposerChild(startOpen: true));

        Assert.Equal("dialog", cut.Find(".oc-bubble").GetAttribute("role"));
        Assert.Contains("hello from provider", cut.Find(".oc-bubble-messages").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_does_not_render_mud_providers()
    {
        var cut = RenderProvider(MiniComposerChild(startOpen: true));

        Assert.Empty(cut.FindComponents<MudThemeProvider>());
        Assert.Empty(cut.FindComponents<MudPopoverProvider>());
        Assert.Empty(cut.FindComponents<MudDialogProvider>());
        Assert.Empty(cut.FindComponents<MudSnackbarProvider>());

        // Child still renders: the host (here: the test) owns the Mud providers.
        Assert.Single(cut.FindAll(".oc-bubble"));
    }

    [Fact]
    public void Provider_disposes_cleanly_with_active_subscribe_loop()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        context.Services.AddSingleton<IOpenConnectClient>(new FakeOpenConnectClient());
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
        context.Services.AddSingleton(NewSession());

        var cut = context.Render<OpenConnectProvider>(parameters => parameters
            .Add(p => p.SessionId, Ulid.NewUlid().ToString())
            .Add(p => p.AutoSubscribe, true)
            .Add(p => p.PollInterval, TimeSpan.FromHours(1))
            .Add(p => p.ChildContent, MiniComposerChild(startOpen: true)));

        Assert.Single(cut.FindAll(".oc-bubble"));

        var ex = Record.Exception(() => context.Dispose());
        Assert.Null(ex);
    }

    private static ComposerSession NewSession(string sessionId = "S1")
    {
        var session = new ComposerSession();
        session.AttachSession(sessionId);
        return session;
    }

    private IRenderedComponent<OpenConnectProvider> RenderProvider(
        RenderFragment childContent,
        string? sessionId = null,
        bool autoSubscribe = false)
    {
        return Render<OpenConnectProvider>(parameters => parameters
            .Add(p => p.SessionId, sessionId)
            .Add(p => p.AutoSubscribe, autoSubscribe)
            .Add(p => p.PollInterval, TimeSpan.FromSeconds(2))
            .Add(p => p.ChildContent, childContent));
    }

    private static RenderFragment MiniComposerChild(bool startOpen) => builder =>
    {
        builder.OpenComponent<MiniComposer>(0);
        builder.AddAttribute(1, nameof(MiniComposer.StartOpen), startOpen);
        builder.CloseComponent();
    };

    private sealed class NoopKeyInterceptorService : IKeyInterceptorService, IDisposable
    {
        public Task SubscribeAsync(IKeyInterceptorObserver observer, KeyInterceptorOptions options) => Task.CompletedTask;

        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, IKeyDownObserver? keyDown = null, IKeyUpObserver? keyUp = null) => Task.CompletedTask;

        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, Action<KeyboardEventArgs>? keyDown = null, Action<KeyboardEventArgs>? keyUp = null) => Task.CompletedTask;

        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, Func<KeyboardEventArgs, Task>? keyDown = null, Func<KeyboardEventArgs, Task>? keyUp = null) => Task.CompletedTask;

        public Task UpdateKeyAsync(IKeyInterceptorObserver observer, KeyOptions options) => Task.CompletedTask;

        public Task UpdateKeyAsync(string elementId, KeyOptions options) => Task.CompletedTask;

        public Task UnsubscribeAsync(IKeyInterceptorObserver observer) => Task.CompletedTask;

        public Task UnsubscribeAsync(string elementId) => Task.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOpenConnectClient : IOpenConnectClient
    {
        public List<string> CreatedTaskMessages { get; } = new();

        public Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
        {
            CreatedTaskMessages.Add(request.Message ?? string.Empty);
            return Task.FromResult(new TaskRecord(
                new TaskId(Ulid.NewUlid()),
                new TenantId(Ulid.NewUlid()),
                new PrincipalId(Ulid.NewUlid()),
                AgentRunState.Accepted));
        }

        public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TaskRecord(
                taskId,
                new TenantId(Ulid.NewUlid()),
                new PrincipalId(Ulid.NewUlid()),
                AgentRunState.Cancelled));
        }

        public Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TaskRecord(
                taskId,
                new TenantId(Ulid.NewUlid()),
                new PrincipalId(Ulid.NewUlid()),
                AgentRunState.Completed));
        }

        public Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SessionRecord(
                sessionId,
                new TenantId(Ulid.NewUlid()),
                new PrincipalId(Ulid.NewUlid()),
                DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(Array.Empty<CapabilityDescriptor>());
        }

        public async IAsyncEnumerable<EventRecord> SubscribeEventsAsync(
            SessionId sessionId,
            long afterSequence = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public async IAsyncEnumerable<CanonicalAgentEvent> ReplayEventsAsync(
            Guid sessionId,
            long afterSequence = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
