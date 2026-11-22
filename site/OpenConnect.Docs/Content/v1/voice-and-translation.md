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

## Built-in SDK Speech-to-Text and Blazor voice bridge

### Host Speech-to-Text (`OpenConnect.Core.Speech` & `OpenConnect.Hosting.AspNetCore`)

Register the SDK's realtime WebSocket and upload Speech-to-Text clients on your ASP.NET Core OpenConnect host with `AddOpenConnectSpeechToText`:

```csharp
using OpenConnect.Hosting.AspNetCore;

builder.Services.AddOpenConnectSpeechToText(builder.Configuration);
```

When `IRealtimeSpeechToTextClient` is configured (and `Workers:Transcription:RealtimeLocalOnly` is not forced to `true`), `MapOpenConnect()` automatically streams browser `openconnect.voice.v1` PCM16 audio directly through the SDK client without requiring a separate transcription worker. Otherwise it falls back to `Workers:Transcription:Endpoint`.

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

`OpenConnectVoiceBridge` enforces same-origin requests, HTTPS (with optional loopback HTTP for local development), single-use 30-second session-bound tickets, and bounded frame/audio limits (`96 KB` per frame, `32 MB` per stream).

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
