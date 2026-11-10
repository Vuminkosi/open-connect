using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Client;
using Xunit;

namespace OpenConnect.Client.Tests;

public sealed class ClientRegistrationAndVadTests
{
    [Fact]
    public void AddOpenConnectClient_OptionsOverload_ConfiguresClientAndReconnectOptions()
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

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<OpenConnectClientOptions>();
        var client = provider.GetRequiredService<IOpenConnectClient>();

        Assert.NotNull(client);
        Assert.Equal(new Uri("https://gateway.example.test/openconnect/v1"), options.ApiBaseAddress);
        Assert.Equal(new Uri("wss://gateway.example.test/openconnect/v1/events"), options.WebSocketAddress);
        Assert.Equal(ClientAuthenticationMode.Host, options.Authentication);
        Assert.True(options.Reconnect.Enabled);
        Assert.True(options.Reconnect.ResumeFromLastCursor);
    }

    [Fact]
    public void AddOpenConnectClient_OptionsOverload_RejectsInvalidAddresses()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddOpenConnectClient(o => o.ApiBaseAddress = new Uri("http://insecure.example.test/openconnect/v1")));

        Assert.Throws<ArgumentException>(() =>
            services.AddOpenConnectClient(o =>
            {
                o.ApiBaseAddress = new Uri("https://gateway.example.test/openconnect/v1");
                o.WebSocketAddress = new Uri("ws://insecure.example.test/openconnect/v1/events");
            }));
    }

    [Fact]
    public void AddOpenConnectClientVad_RegistersSileroVadAndEnforcesMicrophonePermission()
    {
        var services = new ServiceCollection();

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

        using var provider = services.BuildServiceProvider();
        var vadService = provider.GetRequiredService<IClientVadService>();

        Assert.Equal(VadLocation.Client, vadService.Options.Location);
        Assert.Equal(VadEngine.SileroOnnx, vadService.Options.Engine);
        Assert.Throws<InvalidOperationException>(() => vadService.CreateSession(microphonePermissionGranted: false));

        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddOpenConnectClientVad(o => o.RequireMicrophonePermission = false));

        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddOpenConnectClientVad(o => o.ModelAssetBaseUrl = "http://remote.example.test/vad/"));
    }

    [Fact]
    public async Task ClientVadSession_Resamples24kTo16k_PreservesSourceOffsets_ResetsOnGap_AndDisposesCleanly()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectClientVad(o =>
        {
            o.Enabled = true;
            o.Location = VadLocation.Client;
            o.Engine = VadEngine.SileroOnnx;
            o.EnablePreRoll = true;
            o.PreRollMilliseconds = 96; // 3 frames at 32ms
            o.EnableBargeIn = true;
            o.RequireMicrophonePermission = true;
        });

        using var provider = services.BuildServiceProvider();
        var vadService = provider.GetRequiredService<IClientVadService>();
        await using var session = vadService.CreateSession(microphonePermissionGranted: true);

        Assert.Equal(256, session.RecurrentStateLength);
        Assert.Equal(64, session.ContextSampleCount);

        // 768 samples at 24 kHz == 32 ms == 512 samples at 16 kHz.
        var silence24k = new float[768];
        var obs0 = session.ProcessPcmFrame(silence24k, sourceSampleRate: 24_000);
        Assert.Equal(0, obs0.FrameSequence);
        Assert.Equal(0, obs0.SourceSampleOffset);
        Assert.Equal(24_000, obs0.SourceSampleRate);
        Assert.Equal(16_000, obs0.InferenceSampleRate);
        Assert.Equal(512, obs0.SampleCount);
        Assert.False(obs0.IsSpeechCandidate);
        Assert.False(obs0.IsAuthoritativeTurn);

        // Loud speech frame at 24 kHz triggers speech candidate and barge-in epoch increment.
        var speech24k = Enumerable.Repeat(0.6f, 768).ToArray();
        var epochBeforeSpeech = session.CurrentSpeechEpoch;
        var obs1 = session.ProcessPcmFrame(speech24k, sourceSampleRate: 24_000);
        Assert.Equal(1, obs1.FrameSequence);
        Assert.Equal(768, obs1.SourceSampleOffset);
        Assert.True(obs1.IsSpeechCandidate);
        Assert.False(obs1.IsAuthoritativeTurn);
        Assert.True(session.CurrentSpeechEpoch > epochBeforeSpeech);
        Assert.Equal(2, session.GetPreRollFrames().Count);

        // Sequence gap resets internal recurrent state and advances sequence.
        var obsGap = session.ProcessPcmFrame(silence24k, sourceSampleRate: 24_000, explicitFrameSequence: 10);
        Assert.Equal(10, obsGap.FrameSequence);
        Assert.Equal(1536, obsGap.SourceSampleOffset);

        await session.DisposeAsync();
        Assert.True(session.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => session.GetPreRollFrames());
    }

    [Fact]
    public void AddOpenConnectVoiceClient_SeparatesHypothesisFromFinalTurn_GatesRecordingUpload_AndFencesPlaybackEpochs()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectVoiceClient(o =>
        {
            o.EnableLiveTranscription = true;
            o.EnableVoiceRecording = true;
            o.EnablePlayback = true;
            o.EnableBargeIn = true;
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var voice = scope.ServiceProvider.GetRequiredService<IVoiceClientService>();

        Assert.Throws<InvalidOperationException>(() => voice.StartLiveCapture(microphonePermissionGranted: false));

        var liveEpoch = voice.StartLiveCapture(microphonePermissionGranted: true);
        Assert.True(voice.UpdateLiveHypothesis("create a todo", liveEpoch));
        Assert.Equal("create a todo", voice.LivePartialTranscript);
        Assert.Empty(voice.CommittedTranscripts);

        // Stale epoch hypothesis and stale final commit are rejected.
        Assert.False(voice.UpdateLiveHypothesis("stale", liveEpoch - 1));
        Assert.False(voice.CommitGatewayAuthorizedTranscript("stale final", liveEpoch - 1));
        Assert.Empty(voice.CommittedTranscripts);

        // Gateway-authorized final transcript commits exactly once.
        Assert.True(voice.CommitGatewayAuthorizedTranscript("create a todo item", liveEpoch));
        Assert.Equal(string.Empty, voice.LivePartialTranscript);
        Assert.Single(voice.CommittedTranscripts, "create a todo item");

        // Playback epoch fencing & barge-in.
        var playbackEpoch = voice.BeginPlayback();
        Assert.True(voice.TryAcceptPlaybackChunk(playbackEpoch));
        var newPlaybackEpoch = voice.TriggerBargeIn();
        Assert.True(newPlaybackEpoch > playbackEpoch);
        Assert.False(voice.TryAcceptPlaybackChunk(playbackEpoch));

        // Recorded voice preview, discard, and consent-gated submission.
        var wavBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x01, 0x02, 0x03, 0x04 };
        var draft = voice.StageRecordingForPreview(wavBytes, "audio/wav", TimeSpan.FromSeconds(3), microphonePermissionGranted: true);
        Assert.NotNull(voice.StagedRecording);
        Assert.Equal(8, draft.SizeBytes);
        Assert.Equal(64, draft.Sha256Checksum.Length);

        Assert.Throws<InvalidOperationException>(() => voice.AcceptRecordingForSubmission(userConsentGranted: false));
        var submitted = voice.AcceptRecordingForSubmission(userConsentGranted: true);
        Assert.Equal(draft.DraftId, submitted.DraftId);
        Assert.Null(voice.StagedRecording);
    }

    [Fact]
    public async Task AddOpenConnectMauiAudio_ProcessesNative24kMonoPcmThroughSileroVad()
    {
        var services = new ServiceCollection();
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
            o.EnablePreRoll = true;
            o.EnableSilenceSuppression = true;
            o.EnableBargeIn = true;
        });

        using var provider = services.BuildServiceProvider();
        var mauiAudio = provider.GetRequiredService<IMauiAudioCaptureService>();

        Assert.Throws<InvalidOperationException>(() => mauiAudio.StartNativeCaptureSession(platformPermissionGranted: false));

        await using var session = mauiAudio.StartNativeCaptureSession(platformPermissionGranted: true);
        // Two 32ms frames at 24 kHz (768 * 2 = 1536 samples) -> two 512-sample 16 kHz Silero observations.
        var pcm = Enumerable.Repeat(0.5f, 1536).ToArray();
        var observations = mauiAudio.CaptureAndProcessEnhancedPcm(session, pcm);

        Assert.Equal(2, observations.Count);
        Assert.Equal(0, observations[0].SourceSampleOffset);
        Assert.Equal(768, observations[1].SourceSampleOffset);
        Assert.All(observations, static obs =>
        {
            Assert.Equal(16_000, obs.InferenceSampleRate);
            Assert.Equal(512, obs.SampleCount);
            Assert.False(obs.IsAuthoritativeTurn);
        });
    }

    [Fact]
    public void AddOpenConnectNavigatorClient_EnforcesConsentAndManagesTours()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectNavigatorClient(o =>
        {
            o.EnableHighlighting = true;
            o.EnableGuidedTours = true;
            o.RequireConsentForActions = true;
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var navigator = scope.ServiceProvider.GetRequiredService<INavigatorClientService>();

        Assert.False(navigator.CanExecuteAction("todo.submit", userConsentGranted: false));
        Assert.True(navigator.CanExecuteAction("todo.submit", userConsentGranted: true));

        navigator.StartGuidedTour("onboarding", new[] { "todo-list", "todo-editor" });
        Assert.True(navigator.IsTourActive);
        Assert.Equal("todo-list", navigator.HighlightedTargetId);
        Assert.True(navigator.NextTourStep());
        Assert.Equal("todo-editor", navigator.HighlightedTargetId);
        Assert.False(navigator.NextTourStep());
        Assert.False(navigator.IsTourActive);
    }
}
