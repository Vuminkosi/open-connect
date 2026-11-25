using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using OpenConnect.Abstractions;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Components.Contracts;
using Xunit;

namespace OpenConnect.Blazor.Tests;

public sealed class MiniComposerContinuousVoiceTests : OpenConnectBunitContext
{
    public MiniComposerContinuousVoiceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddOpenConnectMudServices();
        Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
        Services.AddSingleton<IOpenConnectClient>(new StubOpenConnectClient());
        Services.AddSingleton(new OpenConnectComponentRegistry());
    }

    private static ComposerSession CreateAttachedSession()
    {
        var session = new ComposerSession();
        session.AttachSession(Ulid.NewUlid().ToString());
        return session;
    }

    private IRenderedComponent<CascadingValue<ComposerSession>> RenderComposer(
        ComposerSession session,
        bool enableContinuousVoice = false,
        bool? voiceInputAvailable = true)
    {
        JSInterop.SetupVoid("mudPopover.dispose").SetVoidResult();
        JSInterop.Setup<int>("mudpopoverHelper.countProviders").SetResult(1);
        return Render<CascadingValue<ComposerSession>>(parameters => parameters
            .Add(p => p.Value, session)
            .Add(p => p.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<MudPopoverProvider>(0);
                builder.CloseComponent();
                builder.OpenComponent<MiniComposer>(1);
                builder.AddAttribute(2, nameof(MiniComposer.StartOpen), true);
                builder.AddAttribute(3, nameof(MiniComposer.EnableContinuousVoice), enableContinuousVoice);
                builder.AddAttribute(4, nameof(MiniComposer.VoiceInputAvailable), voiceInputAvailable);
                builder.CloseComponent();
            })));
    }

    [Fact]
    public void DefaultMode_DoesNotRenderContinuousVoiceBar()
    {
        var cut = RenderComposer(CreateAttachedSession(), enableContinuousVoice: false);
        Assert.Empty(cut.FindAll(".oc-live-voice-bar"));
    }

    [Fact]
    public async Task ContinuousVoice_RendersControls_AccessibleStatus_AndTransitions()
    {
        var session = CreateAttachedSession();
        var cut = RenderComposer(session, enableContinuousVoice: true);

        var toggle = cut.Find("button.oc-live-voice-toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll("button.oc-live-voice-mute"));
        Assert.Empty(cut.FindAll("button.oc-live-voice-stop"));

        // Start continuous live voice session
        toggle.Click();

        Assert.Contains(JSInterop.Invocations, inv => inv.Identifier == "ocComposerVoice.startDuplex");
        Assert.Equal("true", cut.Find("button.oc-live-voice-toggle").GetAttribute("aria-pressed"));
        Assert.NotNull(cut.Find("button.oc-live-voice-mute"));
        Assert.NotNull(cut.Find("button.oc-live-voice-stop"));

        var status = cut.Find(".oc-live-voice-status");
        Assert.Equal("polite", status.GetAttribute("aria-live"));
        Assert.Equal("Listening", status.TextContent.Trim());

        var composer = cut.FindComponent<MiniComposer>();

        // Interim partial transcript updates without committing a turn
        await composer.InvokeAsync(() => composer.Instance.OnVoicePartial("Show my tasks", session.SpeechEpoch));
        Assert.Contains("Show my tasks", cut.Find(".oc-bubble__partial-transcript").TextContent);

        // Final transcript commits turn WITHOUT turning off continuous voice
        await composer.InvokeAsync(() => composer.Instance.OnDuplexVoiceFinal("Show my tasks for this week", session.SpeechEpoch));
        Assert.True(session.IsContinuousVoiceActive);
        Assert.Contains(session.Messages, m => m.Role == "user" && m.Text == "Show my tasks for this week");
        Assert.Equal("Thinking", cut.Find(".oc-live-voice-status").TextContent.Trim());
        Assert.Equal("Thinking…", cut.Find(".oc-status__text").TextContent.Trim());

        // Speaking state shows Interrupt button
        await composer.InvokeAsync(() => composer.Instance.OnDuplexVoiceStateChanged("speaking", session.SpeechEpoch + 1, null));
        Assert.Equal("Speaking", cut.Find(".oc-live-voice-status").TextContent.Trim());
        Assert.NotNull(cut.Find("button.oc-live-voice-interrupt"));

        // Awaiting confirmation state
        await composer.InvokeAsync(() => composer.Instance.OnDuplexVoiceStateChanged("awaiting_confirmation", session.SpeechEpoch + 1, null));
        Assert.Equal("Awaiting confirmation", cut.Find(".oc-live-voice-status").TextContent.Trim());

        // Mute / Unmute toggles cleanly
        cut.Find("button.oc-live-voice-mute").Click();
        Assert.Contains(JSInterop.Invocations, inv => inv.Identifier == "ocComposerVoice.setMuted");
        Assert.Equal("Muted", cut.Find(".oc-live-voice-status").TextContent.Trim());

        // Stop button tears down session
        cut.Find("button.oc-live-voice-stop").Click();
        Assert.Contains(JSInterop.Invocations, inv => inv.Identifier == "ocComposerVoice.stopSession");
        Assert.False(session.IsContinuousVoiceActive);
    }

    [Fact]
    public void PermissionDenied_ShowsRealReason_AndRetainsTextInput()
    {
        JSInterop.SetupVoid("ocComposerVoice.startDuplex", _ => true)
            .SetException(new JSException("NotAllowedError: Permission denied"));

        var session = CreateAttachedSession();
        var cut = RenderComposer(session, enableContinuousVoice: true);

        cut.Find("button.oc-live-voice-toggle").Click();

        Assert.False(session.IsContinuousVoiceActive);
        Assert.Equal("Unavailable", cut.Find(".oc-live-voice-status").TextContent.Trim());
        Assert.Contains("Microphone permission was denied", cut.Find(".oc-live-voice-error").TextContent);
        // Text input remains enabled and accessible
        Assert.NotEmpty(cut.FindAll(".oc-bubble__input-row input, .oc-bubble__input-row textarea"));
    }

    [Fact]
    public async Task DisposeAsync_StopsActiveContinuousVoiceSessionAndReleasesState()
    {
        var session = CreateAttachedSession();
        var cut = RenderComposer(session, enableContinuousVoice: true);

        cut.Find("button.oc-live-voice-toggle").Click();
        Assert.True(session.IsContinuousVoiceActive);

        var composer = cut.FindComponent<MiniComposer>();
        await composer.Instance.DisposeAsync();

        Assert.Contains(JSInterop.Invocations, inv => inv.Identifier == "ocComposerVoice.stopSession");
        Assert.False(session.IsContinuousVoiceActive);
        Assert.Equal(ComposerMicrophoneState.Off, session.MicrophoneState);
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
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubOpenConnectClient : IOpenConnectClient
    {
        public Task<SessionRecord> CreateSessionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionRecord(new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), DateTimeOffset.UtcNow));

        public Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionRecord(sessionId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), DateTimeOffset.UtcNow));

        public Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(new TaskId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Accepted));

        public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(taskId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Cancelled));

        public Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(taskId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Completed));

        public Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(Array.Empty<CapabilityDescriptor>());

        public Task<SpeechSynthesisResult> SynthesizeSpeechAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new SpeechSynthesisResult("af_heart", "pcm", 24_000, 1, string.Empty));

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
