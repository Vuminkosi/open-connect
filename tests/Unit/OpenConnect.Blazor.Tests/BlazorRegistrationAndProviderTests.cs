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

public sealed class BlazorRegistrationAndProviderTests : BunitContext
{
    public BlazorRegistrationAndProviderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IKeyInterceptorService, NoopKeyInterceptorService>();
    }

    [Fact]
    public void FullBlazorAndMauiClientRegistration_MatchesSpecSection3And4()
    {
        var services = new ServiceCollection();

        services.AddOpenConnectClient(o =>
        {
            o.ApiBaseAddress = new Uri("https://gateway.example.test/openconnect/v1");
            o.WebSocketAddress = new Uri("wss://gateway.example.test/openconnect/v1/events");
            o.Authentication = ClientAuthenticationMode.Host;
            o.Reconnect.Enabled = true;
            o.Reconnect.ResumeFromLastCursor = true;
        });

        services.AddOpenConnectComposer(o =>
        {
            o.Enabled = true;
            o.Position = ComposerPosition.BottomRight;
            o.EnableTaskProgress = true;
            o.EnableComponentDiscovery = true;
            o.EnableVoiceInput = true;
            o.EnableVoiceOutput = true;
        });

        services.AddOpenConnectMauiAudio(o =>
        {
            o.SampleRate = 24_000;
            o.Channels = 1;
            o.RequireMicrophonePermission = true;
        });

        services.AddOpenConnectClientVad(o =>
        {
            o.Enabled = true;
            o.Location = VadLocation.Client;
            o.Engine = VadEngine.SileroOnnx;
            o.ModelAssetBaseUrl = "/assets/openconnect/vad/";
            o.EnablePreRoll = true;
            o.EnableSilenceSuppression = true;
            o.EnableBargeIn = true;
            o.RequireMicrophonePermission = true;
        });

        services.AddOpenConnectVoiceClient(o =>
        {
            o.EnableLiveTranscription = true;
            o.EnableVoiceRecording = true;
            o.EnablePlayback = true;
            o.EnableBargeIn = true;
        });

        services.AddOpenConnectNavigatorClient(o =>
        {
            o.EnableHighlighting = true;
            o.EnableGuidedTours = true;
            o.RequireConsentForActions = true;
        });

        services.AddOpenConnectComponent<TodoListComponent>();
        services.AddOpenConnectComponent<TodoEditorComponent>();
        services.AddOpenConnectComponent<TodoProgressComponent>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var composerOptions = sp.GetRequiredService<OpenConnectComposerOptions>();
        Assert.True(composerOptions.Enabled);
        Assert.Equal(ComposerPlacement.BottomRight, composerOptions.Position);
        Assert.True(composerOptions.EnableTaskProgress);
        Assert.True(composerOptions.EnableComponentDiscovery);
        Assert.True(composerOptions.EnableVoiceInput);
        Assert.True(composerOptions.EnableVoiceOutput);

        var registry = sp.GetRequiredService<OpenConnectComponentRegistry>();
        foreach (var cfg in sp.GetServices<IConfigureOpenConnectComponent>())
        {
            cfg.Apply(registry);
        }

        Assert.True(registry.IsRegistered("todo-list"));
        Assert.True(registry.IsRegistered("todo-editor"));
        Assert.True(registry.IsRegistered("todo-progress"));
    }

    [Fact]
    public void OpenConnectProvider_ComposesMiniComposerAndOpenConnectNavigatorProvider_AndRejectsNestedProvider()
    {
        Services.AddOpenConnectComposer(o =>
        {
            o.Enabled = true;
            o.Position = ComposerPosition.BottomRight;
        });
        Services.AddOpenConnectNavigatorClient(o =>
        {
            o.EnableHighlighting = true;
            o.EnableGuidedTours = true;
            o.RequireConsentForActions = true;
        });
        Services.AddOpenConnectComponent<TodoListComponent>();
        Services.AddSingleton<IOpenConnectClient>(new FakeOpenConnectClient());

        var sessionId = Ulid.NewUlid().ToString();
        var cut = Render<OpenConnectProvider>(parameters => parameters
            .Add(p => p.SessionId, sessionId)
            .Add(p => p.AutoSubscribe, false)
            .Add(p => p.ChildContent, builder =>
            {
                builder.OpenComponent<MiniComposer>(0);
                builder.AddAttribute(1, nameof(MiniComposer.StartOpen), true);
                builder.CloseComponent();

                builder.OpenComponent<OpenConnectNavigatorProvider>(2);
                builder.CloseComponent();
            }));

        Assert.Single(cut.FindAll(".oc-bubble"));
        var navigator = Services.GetRequiredService<INavigatorClientService>();
        navigator.Highlight("todo-list");
        cut.WaitForAssertion(() =>
        {
            var status = cut.Find(".oc-navigator-host");
            Assert.Equal("todo-list", status.GetAttribute("data-oc-navigator-highlight"));
        });

        // Nested OpenConnectProvider must fail fast.
        Assert.Throws<InvalidOperationException>(() =>
            Render<OpenConnectProvider>(parameters => parameters
                .Add(p => p.AutoSubscribe, false)
                .Add(p => p.ChildContent, inner =>
                {
                    inner.OpenComponent<OpenConnectProvider>(0);
                    inner.AddAttribute(1, nameof(OpenConnectProvider.AutoSubscribe), false);
                    inner.CloseComponent();
                })));
    }

    [Fact]
    public void OpenConnectProvider_ResumesFromLastCursorAfterReconnect_WithoutDuplicatingEvents()
    {
        var sessionUlid = Ulid.NewUlid();
        var observedCursors = new List<long>();
        var callCount = 0;

        var fakeClient = new CursorTrackingOpenConnectClient((sid, afterSeq) =>
        {
            observedCursors.Add(afterSeq);
            callCount++;
            if (callCount == 1)
            {
                return new[]
                {
                    new EventRecord(sid, 1, "message.delta", "Hello"),
                    new EventRecord(sid, 2, "message.completed", string.Empty),
                };
            }

            if (callCount == 2)
            {
                throw new HttpRequestException("Simulated transient WS/poll disconnect");
            }

            return new[]
            {
                new EventRecord(sid, 2, "message.completed", string.Empty), // stale duplicate
                new EventRecord(sid, 3, "message.delta", "World"),
            };
        });

        Services.AddOpenConnectClient(o =>
        {
            o.ApiBaseAddress = new Uri("https://gateway.example.test/openconnect/v1");
            o.WebSocketAddress = new Uri("wss://gateway.example.test/openconnect/v1/events");
            o.Authentication = ClientAuthenticationMode.Host;
            o.Reconnect.Enabled = true;
            o.Reconnect.ResumeFromLastCursor = true;
        });
        Services.AddOpenConnectComposer();
        Services.AddSingleton<IOpenConnectClient>(fakeClient);

        var cut = Render<OpenConnectProvider>(parameters => parameters
            .Add(p => p.SessionId, sessionUlid.ToString())
            .Add(p => p.AutoSubscribe, true)
            .Add(p => p.PollInterval, TimeSpan.FromMilliseconds(15)));

        var session = Services.GetRequiredService<ComposerSession>();
        cut.WaitForAssertion(() =>
        {
            Assert.True(observedCursors.Count >= 2);
            Assert.Equal(0, observedCursors[0]);
            Assert.Equal(2, observedCursors[1]);
        });

        // Force immediate reconnect after the simulated transient failure.
        cut.Instance.Reconnect();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, session.LastSequence);
            Assert.Single(session.Messages);
            Assert.Equal("Hello", session.Messages[0].Text);
            Assert.Equal("World", session.StreamingText);
        });
    }

    [Fact]
    public void ComposerSession_DistinguishesLiveSttFromRecordedVoice_AndFencesSpeechEpochs()
    {
        var session = new ComposerSession();
        session.AttachSession("S1");

        // Permission denial fails closed.
        session.StartLiveListening(permissionGranted: false);
        Assert.Equal(ComposerMicrophoneState.Failed, session.MicrophoneState);
        Assert.Equal("Microphone permission denied.", session.LastError);

        // Live listening: partial hypotheses do not commit user messages.
        var liveEpoch = session.StartLiveListening(permissionGranted: true);
        Assert.Equal(ComposerMicrophoneState.ListeningLive, session.MicrophoneState);
        Assert.True(session.UpdateLiveHypothesis("buy milk", liveEpoch));
        Assert.Equal("buy milk", session.LivePartialTranscript);
        Assert.Empty(session.Messages);

        // Stale epoch is rejected.
        Assert.False(session.UpdateLiveHypothesis("stale", liveEpoch - 1));
        Assert.False(session.CommitLiveTranscript("stale commit", liveEpoch - 1));

        // Gateway-authorized final transcript commits single user turn.
        Assert.True(session.CommitLiveTranscript("buy milk and eggs", liveEpoch));
        Assert.Equal(string.Empty, session.LivePartialTranscript);
        Assert.Equal(ComposerMicrophoneState.Off, session.MicrophoneState);
        Assert.Single(session.Messages);
        Assert.Equal("buy milk and eggs", session.Messages[0].Text);

        // Playback epoch fencing & barge-in.
        var playbackEpoch = session.BeginSpeechPlayback();
        Assert.True(session.TryAcceptTtsAudioChunk(playbackEpoch));
        session.TriggerBargeIn();
        Assert.False(session.TryAcceptTtsAudioChunk(playbackEpoch));

        // Recorded voice preview/discard/consent-gated submit.
        session.StartVoiceRecording(permissionGranted: true);
        Assert.Equal(ComposerMicrophoneState.Recording, session.MicrophoneState);
        var draft = session.StageRecordedVoiceForPreview(new byte[] { 1, 2, 3, 4 }, "audio/wav", TimeSpan.FromSeconds(2));
        Assert.NotNull(session.StagedRecording);
        Assert.Throws<InvalidOperationException>(() => session.AcceptRecordedVoiceForSubmission(userConsentGranted: false));
        var accepted = session.AcceptRecordedVoiceForSubmission(userConsentGranted: true);
        Assert.Equal(draft.DraftId, accepted.DraftId);
        Assert.Null(session.StagedRecording);
        Assert.Equal(ComposerMicrophoneState.Transcribing, session.MicrophoneState);
    }

    private sealed class TodoListComponent : ComponentBase;
    private sealed class TodoEditorComponent : ComponentBase;
    private sealed class TodoProgressComponent : ComponentBase;

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

    private sealed class FakeOpenConnectClient : IOpenConnectClient
    {
        public Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(new TaskId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Accepted));

        public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(taskId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Cancelled));

        public Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(taskId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Completed));

        public Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionRecord(sessionId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(Array.Empty<CapabilityDescriptor>());

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

    private sealed class CursorTrackingOpenConnectClient(Func<SessionId, long, IEnumerable<EventRecord>> onSubscribe) : IOpenConnectClient
    {
        public Task<TaskRecord> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(new TaskId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Accepted));

        public Task<TaskRecord> CancelTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(taskId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Cancelled));

        public Task<TaskRecord> GetTaskAsync(TaskId taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaskRecord(taskId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), AgentRunState.Completed));

        public Task<SessionRecord> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionRecord(sessionId, new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<CapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CapabilityDescriptor>>(Array.Empty<CapabilityDescriptor>());

        public async IAsyncEnumerable<EventRecord> SubscribeEventsAsync(
            SessionId sessionId,
            long afterSequence = 0,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in onSubscribe(sessionId, afterSequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
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
