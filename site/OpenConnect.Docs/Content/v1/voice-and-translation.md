---
title: Voice and translation
version: v1
summary: Configure speech and translation as optional, separately deployed capabilities.
order: 76
---

# Voice and translation

Voice input, transcription, speech synthesis and translation are optional. A client UI, a running gateway and a model-backed worker are separate components. Select the operation, verify its deployment and readiness, and obtain the necessary caller and microphone permissions before enabling a user journey.

## Speech input and output

The .NET client exposes `SynthesizeSpeechAsync` and the gateway maps `POST /openconnect/v1/speech/synthesize`. A host must configure and authorize an appropriate synthesis service; mapping the route does not install a voice. Text sent through the .NET client must contain 1 to 2,000 characters. Check failures and cancellation and avoid logging text or generated audio.

```csharp
using OpenConnect.Client;

// client is an authenticated IOpenConnectClient; the gateway must have speech configured.
var speech = await client.SynthesizeSpeechAsync(
    new SpeechSynthesisRequest("Hello", VoiceId: "af_heart", Locale: "en-US"),
    cancellationToken);
byte[] pcm = Convert.FromBase64String(speech.AudioBase64);
Console.WriteLine($"{speech.SampleRateHz} Hz, {speech.Channels} channel(s)");
// Send pcm to an audio player configured for speech.Format, sample rate and channels.
```

For client voice features, `AddOpenConnectVoiceClient` registers the voice client service; `AddOpenConnectClientVad` configures client voice activity detection. The VAD options validate microphone permission and supported sample/frame settings. MAUI hosts can register `AddOpenConnectMauiAudio` and must obtain platform microphone permission. These registrations alone do not supply a transcription worker, model or user consent flow.

```csharp
using OpenConnect.Client;

builder.Services.AddOpenConnectVoiceClient();
builder.Services.AddOpenConnectClientVad(options =>
{
    options.RequireMicrophonePermission = true;
    options.InferenceSampleRate = 16_000;
    options.FrameSamples = 512;
});
```

For a local transcription worker, provision a compatible model and an integrity manifest before enabling the real backend. `workers/transcription/README.md` describes the importer, required model files and environment variables. The worker does not download models automatically; synthetic engines in tests do not establish transcription accuracy. `workers/kokoro/README.md` documents synthesis model and voice provisioning. Confirm license, integrity, readiness and hardware compatibility for the actual model in use.

The documentation site's local assistant selects the transcription worker's local-only realtime route with `Workers:Transcription:RealtimeLocalOnly`, or uses the SDK's built-in Speech-to-Text provider when `AddOpenConnectSpeechToText` is configured. The realtime route accepts authenticated mono 24 kHz PCM16 and returns partial and final transcripts. If the provider or connection is unavailable, the composer reports the failure and text input remains available. Successful voice-driven LiveUI actions also require the composer's active session connection.

## Built-in SDK Speech-to-Text, Silero VAD, and Blazor voice bridge

### Host Speech-to-Text and Silero ONNX VAD (`OpenConnect.Core.Speech` & `OpenConnect.Hosting.AspNetCore`)

Register the SDK's realtime WebSocket and upload Speech-to-Text clients together with the shared Silero ONNX `IVoiceActivityDetector` on your ASP.NET Core OpenConnect host with `AddOpenConnectSpeechToText`:

```csharp
using OpenConnect.Hosting.AspNetCore;

builder.Services.AddOpenConnectSpeechToText(builder.Configuration);
```

`AddOpenConnectSpeechToText` also registers `SileroOnnxVoiceActivityDetector` (`silero_vad_16k_op15.onnx`, 16 kHz 512-sample frames with isolated per-stream recurrent state) via `IVoiceActivityDetector` — or register it standalone with `AddOpenConnectSileroVad()`. When `IRealtimeSpeechToTextClient` is configured (and `Workers:Transcription:RealtimeLocalOnly` is not forced to `true`), `MapOpenConnect()` evaluates incoming `openconnect.voice.v1` PCM16 audio through `SileroVoiceActivityAudioStream` and streams it directly through the SDK client without requiring a separate transcription worker. Otherwise it falls back to `Workers:Transcription:Endpoint`.

On the browser side, `MiniComposer` (`OpenConnect.Components.Blazor`) bundles Silero VAD v5 (`silero_vad_v5.onnx` + ONNX Runtime Web WASM) to detect speech start, buffer pre-roll audio, and auto-stop after trailing silence while driving the loudness-reactive microphone glow, with automatic RMS fallback if WASM is unavailable.

### Blazor Server voice bridge (`OpenConnect.Components.Blazor.Voice`)

When a Blazor Server application hosts `MiniComposer` separately from its OpenConnect gateway, register the SDK voice bridge so the browser connects to a same-origin WebSocket using a short-lived, single-use `oc-ticket.<ticket>` subprotocol instead of receiving server-side Bearer tokens:

```csharp
using OpenConnect.Components.Blazor.Voice;

builder.Services.AddOpenConnectVoiceBridge(options =>
{
    options.GatewayBaseAddress = new Uri("https://gateway.example.internal/");
    options.AllowLoopbackHttp = builder.Environment.IsDevelopment();
});
```

Then pass a ticket callback to `MiniComposer`:

```razor
@inject OpenConnectVoiceTicketStore VoiceTickets

<MiniComposer VoiceConnectionTicket="@(sessionId => VoiceTickets.Issue(sessionId, serverBearerToken))" />
```

`OpenConnectVoiceBridge` enforces same-origin requests, HTTPS (with optional loopback HTTP for local development), single-use 30-second session-bound tickets, and bounded frame/audio limits (`96 KB` per frame; `32 MB` per stream on `openconnect.voice.v1`, or a bounded rolling session budget on `openconnect.voice.v2`).

## Continuous Live Voice (`openconnect.voice.v2`)

Continuous Live Voice is an **opt-in** full-duplex mode negotiated over WebSocket subprotocol `openconnect.voice.v2`. Existing push-to-talk and one-shot speech synthesis (`openconnect.voice.v1` and `POST /openconnect/v1/speech/synthesize`) remain the default and are unchanged unless duplex capabilities are explicitly enabled on both host and client.

| Mode | Subprotocol | Behavior |
|---|---|---|
| Legacy v1 (default) | `openconnect.voice.v1` | One-shot capture per utterance, single final transcript submission, buffered one-shot speech synthesis. |
| Continuous Live Voice (opt-in) | `openconnect.voice.v2` | Persistent microphone session across multiple turns, 32-byte little-endian binary PCM16 framing, incremental phrase-level TTS playback, adaptive endpointing, and playback-aware barge-in interruption. |

### Host registration (`OpenConnectVoiceOptions`)

Enable duplex conversation, incremental speech output, semantic/adaptive turn detection, and rolling session budgets on the ASP.NET Core host via `OpenConnectVoiceOptions`. Client flags never grant capability or authorization on their own — the gateway remains authoritative for tenant/principal authorization, privacy screening, and `SpeechEpoch` fencing.

```csharp
using OpenConnect.Core.Configuration;

builder.Services.Configure<OpenConnectVoiceOptions>(options =>
{
    options.EnableContinuousConversation = true;
    options.EnableStreamingSpeechOutput = true;
    options.EnableSemanticTurnDetection = true;
    options.MaxUtteranceAudioBytes = 32L * 1024 * 1024;
    options.MaxRollingSessionAudioBytes = 256L * 1024 * 1024;
    options.RollingSessionWindowSeconds = 3600;
    options.MaxBufferedOutputFrames = 256;
    options.PhraseFlushTimeoutMs = 350;
    options.MinPhraseWords = 8;
    options.MaxPhraseWords = 25;
    options.ConservativeEndpointing = false;
});
```

### Client registration (`AddOpenConnectVoiceClient`, `IRealtimeVoiceClient`, `IRealtimeVoiceSession`)

Opt into continuous conversation and streaming playback on the .NET client with `AddOpenConnectVoiceClient`, then open a duplex session via `IRealtimeVoiceClient` to obtain an `IRealtimeVoiceSession`:

```csharp
using OpenConnect.Abstractions.Speech;
using OpenConnect.Client;

builder.Services.AddOpenConnectVoiceClient(options =>
{
    options.EnablePlayback = true;
    options.EnableBargeIn = true;
    options.EnableContinuousConversation = true;
    options.EnableStreamingPlayback = true;
    options.EnableSemanticTurnDetection = true;
});
```

```csharp
// Open an authenticated duplex voice session (openconnect.voice.v2)
await using IRealtimeVoiceSession session = await realtimeVoiceClient.ConnectAsync(
    sessionId,
    new VoiceDuplexSessionOptions(EnableContinuousConversation: true, EnableStreamingPlayback: true),
    cancellationToken);

await session.SendAudioAsync(pcm16Frame, cancellationToken);

await foreach (var evt in session.ReadEventsAsync(cancellationToken))
{
    // Handle partial/final transcripts, screened audio segments, and epoch acknowledgements.
}
```

In Blazor applications using `MiniComposer`, enable continuous voice explicitly with `EnableContinuousVoice="true"` (default `false`):

```razor
<MiniComposer EnableContinuousVoice="true"
              VoiceConnectionTicket="@(sessionId => VoiceTickets.Issue(sessionId, serverBearerToken))" />
```

### Speech-to-Text routing (Cloud Muse primary & local Whisper fallback)

- **Cloud Muse primary**: When cloud speech-to-text is configured and permitted by egress policy, the gateway routes realtime audio to `muse-voice-transcribe-1.0` in `ENDPOINTING` dialogue mode for low-latency streaming partials and dialogue endpointing.
- **Local Whisper fallback / local-only mode**: When `Workers:Transcription:RealtimeLocalOnly` is `true`, or when cloud egress is disabled or unconfigured, the gateway streams PCM16 frames to the local transcription worker's rolling-window Whisper decoder. Partial hypotheses emit monotonic revisions and never trigger tool execution; only the authoritative final transcript is admitted as a turn.

### Incremental Kokoro PCM16 streaming and local vs cloud availability

- **Local incremental synthesis**: The local Kokoro 82M worker validates the requested voice against its verified voice registry (`af_heart` default) and yields 24 kHz mono PCM16 chunks incrementally per privacy-screened phrase segment. Every phrase is screened by the .NET privacy firewall before synthesis — unscreened audio is never released to reduce latency.
- **Local vs cloud availability**: Incremental PCM16 streaming is available on the local/self-hosted Kokoro path first. Cloud-hosted TTS remains buffered until segment-level claim, privacy evidence, and quota settlement are completed (P1); existing cloud voice claims, consumption, and privacy evidence remain intact.

### Adaptive endpointing, playback-aware barge-in, and known limitations

- **Adaptive endpointing**: Combines Silero VAD silence duration with transcript stability, punctuation, hesitation markers (such as `um` / `uh` / `umm`), and backchannel filtering (`mhm` / `uh-huh`) during playback so natural pauses do not prematurely split a turn. Set `ConservativeEndpointing = true` for noisier environments.
- **Playback-aware barge-in interruption**: When the user speaks over assistant audio or clicks stop, the browser clears its `AudioWorklet` playback buffer immediately without waiting for a gateway round trip, locally suppresses the active `SpeechEpoch`, retains mic pre-roll, and sends `response.interrupt` with the rendered sample cursor. The gateway advances `SpeechEpoch` atomically once, cancels pending TTS/generation where supported, and reconciles confirmed-played versus uncertain-partially-played segments into future conversational context without rolling back committed tool effects.
- **Known limitations**: OpenConnect Live Voice is a cascaded **STT → Agent/LLM → Privacy Firewall → TTS** pipeline, not a native audio-to-audio model. End-to-end latency includes endpointing, transcription finalization, first-phrase reasoning, fail-closed policy screening, and incremental synthesis. Do not claim parity with native speech-to-speech systems without measured audio-level benchmarks on the target hardware and network profile.

## Translation

The NLLB worker sample under `samples/workers/nllb` documents its local container, capability response and test commands. The sample's fake engine is for development and contract tests. The available language pairs, model revision and licensing conditions depend on the configured worker and model; inspect its capability response and license before presenting translation as available. A ready translation process is not authorization to send it sensitive content.

## Handle an audio request safely

1. Ask for microphone permission and explain when recording is active. Stop capture when the user ends the session or permission is revoked.
2. Authenticate the caller, authorize the requested operation and apply the host's modality and egress policies. Protect audio in transit and at rest.
3. Submit through the configured host and worker integration. Show a meaningful unavailable or denied state if the worker, model or policy is not ready.
4. Treat transcripts and synthesized audio as sensitive results. Apply retention rules and review output before using it for consequential decisions.

Test capture, device permissions, cancellation, interrupted playback and the real worker on the target platform. A successful registration or a synthetic worker test is not evidence of microphone, model or provider behavior.

## Related links

- [Gateway HTTP API](/docs/v1/rest-api)
- [NATS and worker integration](/docs/v1/nats-and-workers)
