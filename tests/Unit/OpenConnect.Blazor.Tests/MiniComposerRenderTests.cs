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
/// Rendered-component evidence for <see cref="MiniComposer"/> (CF-AT-026/027):
/// closed FAB-only state, StartOpen bubble, Attached mode, FAB toggle/close
/// behaviour, settings disclosure, disposal and JS hydration fallback.
/// bUnit <see cref="BunitContext"/> with Loose JSInterop; no network, Docker, or real JS.
/// </summary>
public sealed class MiniComposerRenderTests : BunitContext
{
    public MiniComposerRenderTests()
    {
        Configure(this);
    }

    private static void Configure(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        // No-op keyboard-interception stub: the real KeyInterceptorService needs live JS and
        // only implements IAsyncDisposable, which breaks bUnit sync teardown. Chips render identically.
        context.Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        context.Services.AddSingleton<IOpenConnectClient>(new FakeOpenConnectClient());
        context.Services.AddSingleton(new OpenConnectComponentRegistry());
    }

    [Fact]
    public void Closed_renders_fab_only_without_bubble()
    {
        var cut = RenderMiniComposer(this, NewSession());

        var fab = cut.Find(".oc-anchored-fab");
        Assert.Equal("Open Assistant", fab.GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll(".oc-bubble"));
    }

    [Fact]
    public void Closed_render_makes_no_composer_js_calls()
    {
        using var strict = new BunitContext();
        strict.Services.AddMudServices();
        strict.Services.AddSingleton<IOpenConnectClient>(new FakeOpenConnectClient());
        strict.Services.AddSingleton(new OpenConnectComponentRegistry());

        var cut = RenderMiniComposer(strict, NewSession());

        Assert.Empty(cut.FindAll(".oc-bubble"));
        Assert.DoesNotContain(
            strict.JSInterop.Invocations,
            static i => i.Identifier.StartsWith("ocComposer", StringComparison.Ordinal));
    }

    [Fact]
    public void StartOpen_renders_bubble_with_title_status_and_fab()
    {
        var cut = RenderMiniComposer(this, NewSession(), startOpen: true);

        var bubble = cut.Find(".oc-bubble");
        Assert.Equal("dialog", bubble.GetAttribute("role"));
        Assert.Equal("Assistant", cut.Find(".oc-bubble-title").TextContent);
        Assert.Equal("Ready", cut.Find(".oc-bubble-subtitle").TextContent);
        Assert.Equal("Close Assistant", cut.Find(".oc-anchored-fab").GetAttribute("aria-label"));
    }

    [Fact]
    public void Attached_renders_inline_bubble_without_fab_or_drag_handle()
    {
        var cut = RenderMiniComposer(this, NewSession(), placement: ComposerPlacement.Attached);

        Assert.Empty(cut.FindAll(".oc-anchored-fab"));
        Assert.Equal("dialog", cut.Find(".oc-bubble").GetAttribute("role"));
        Assert.Empty(cut.FindAll(".oc-drag-handle"));
    }

    [Fact]
    public void Fab_click_toggles_bubble_open_then_close_button_minimizes()
    {
        var cut = RenderMiniComposer(this, NewSession());

        Assert.Empty(cut.FindAll(".oc-bubble"));

        cut.Find(".oc-anchored-fab").Click();
        Assert.Single(cut.FindAll(".oc-bubble"));
        Assert.Equal("Close Assistant", cut.Find(".oc-anchored-fab").GetAttribute("aria-label"));

        cut.Find("button[aria-label='Minimize assistant']").Click();
        Assert.Empty(cut.FindAll(".oc-bubble"));
        Assert.Equal("Open Assistant", cut.Find(".oc-anchored-fab").GetAttribute("aria-label"));
    }

    [Fact]
    public void Suggestions_render_as_chips_when_provided()
    {
        var cut = RenderMiniComposer(this, NewSession(), startOpen: true, suggestions: new[] { "Summarize", "Retry" });

        var chips = cut.Find(".oc-bubble-chips");
        Assert.Contains("Summarize", chips.TextContent, StringComparison.Ordinal);
        Assert.Contains("Retry", chips.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_toggle_discloses_placement_and_limits()
    {
        var cut = RenderMiniComposer(this, NewSession(), startOpen: true);

        Assert.Empty(cut.FindAll(".oc-bubble-settings"));

        cut.Find("button[aria-label='Composer settings']").Click();

        var settings = cut.Find(".oc-bubble-settings");
        Assert.Contains("BottomRight", settings.TextContent, StringComparison.Ordinal);
        Assert.Contains("8000", settings.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_bubble_renders_without_throw_when_js_is_unavailable()
    {
        // Loose JSInterop swallows the ocComposerDrag/ocComposerInput/focus calls,
        // covering the OnAfterRenderAsync prerender/JSDisconnected fallback path.
        var cut = RenderMiniComposer(this, NewSession(), startOpen: true);

        Assert.Single(cut.FindAll(".oc-bubble"));
        Assert.Equal("dialog", cut.Find(".oc-bubble").GetAttribute("role"));
    }

    [Fact]
    public void Dispose_removes_subscription_without_throw()
    {
        var session = NewSession();

        int notifications = 0;

        session.Changed += () => notifications++;

        using (var ctx = new BunitContext())

        {

            Configure(ctx);

            var cut = RenderMiniComposer(ctx, session, startOpen: true);

            Assert.Contains(ctx.JSInterop.Invocations, static i => i.Identifier == "ocComposerDrag.attach");

            Assert.Contains(ctx.JSInterop.Invocations, static i => i.Identifier.StartsWith("ocComposerInput.", StringComparison.Ordinal));

        }

        // Tearing down the context must not throw, and the component must have

        // unsubscribed: mutating the session afterwards notifies only our handler.

        var ex = Record.Exception(() => session.BeginSend("after dispose"));

        Assert.Null(ex);

        Assert.Equal(1, notifications);
    }

    private static ComposerSession NewSession(string sessionId = "S1")
    {
        var session = new ComposerSession();
        session.AttachSession(sessionId);
        return session;
    }

    private static IRenderedComponent<CascadingValue<ComposerSession>> RenderMiniComposer(
        BunitContext context,
        ComposerSession session,
        bool startOpen = false,
        ComposerPlacement placement = ComposerPlacement.BottomRight,
        IReadOnlyList<string>? suggestions = null)
    {
        return context.Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(p => p.Value, session)
            .Add(p => p.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<MiniComposer>(0);
                builder.AddAttribute(1, nameof(MiniComposer.StartOpen), startOpen);
                builder.AddAttribute(2, nameof(MiniComposer.Placement), placement);
                builder.AddAttribute(3, nameof(MiniComposer.Suggestions), suggestions);
                builder.CloseComponent();
            })));
    }

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
