using Microsoft.Extensions.Configuration;

namespace OpenConnect.Docs.Services.Voice;

/// <summary>
/// Builds sanitized Live Voice benchmark and readiness snapshots for the documentation portal.
/// Engineering targets are always presented separately from measured telemetry; production
/// metrics are never fabricated.
/// </summary>
public sealed class LiveVoiceBenchmarkService
{
    public const string NotYetMeasured = "Not yet measured";

    private readonly IConfiguration? _configuration;

    public LiveVoiceBenchmarkService(IConfiguration? configuration = null)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Creates the default baseline snapshot with engineering targets separated from unmeasured runtime metrics.
    /// </summary>
    public LiveVoiceBenchmarkSnapshot CreateBaselineSnapshot(
        bool assistantConfigured,
        bool? voiceReady,
        bool voiceChecking)
    {
        var localOnly = _configuration?.GetValue<bool>("Workers:Transcription:RealtimeLocalOnly") ?? false;
        var cloudConfigured = !string.IsNullOrWhiteSpace(_configuration?["OpenConnect:Cloud:Identity:Authority"])
            || !string.IsNullOrWhiteSpace(_configuration?["ConnectionStrings:CloudControlPlane"]);
        var deploymentMode = localOnly
            ? "Local-only (no external speech egress)"
            : cloudConfigured
                ? "Hybrid / Cloud-assisted (local TTS streaming; cloud TTS buffered until P1 settlement)"
                : "Local / Self-hosted";

        var readinessState = voiceChecking
            ? "Checking"
            : voiceReady switch
            {
                true => "Ready",
                false => "Unavailable",
                null => assistantConfigured ? "Not probed" : "Not configured"
            };

        var sttProvider = localOnly
            ? "Local Whisper (rolling-window streaming)"
            : "Cloud Muse (muse-voice-transcribe-1.0, ENDPOINTING) primary / Local Whisper fallback";

        var providers = new List<LiveVoiceProviderStatus>
        {
            new(
                Role: "Speech-to-Text (STT)",
                Provider: sttProvider,
                Mode: localOnly ? "Local streaming" : "Cloud primary + local fallback",
                State: readinessState,
                Detail: localOnly
                    ? "Local-only rolling decode; zero external audio egress."
                    : "Uses Cloud Muse ENDPOINTING dialogue mode when configured and policy-permitted; falls back to local Whisper streaming."),
            new(
                Role: "Text-to-Speech (TTS)",
                Provider: "Kokoro 82M (af_heart default)",
                Mode: cloudConfigured ? "Local incremental PCM16 / Cloud buffered" : "Local incremental PCM16 streaming",
                State: readinessState,
                Detail: "Emits validated 24 kHz PCM16 chunks per screened phrase locally. Cloud TTS remains buffered until P1 segment-level settlement."),
            new(
                Role: "Voice Activity Detection (VAD)",
                Provider: "Silero ONNX (silero_vad_16k_op15.onnx / browser v5 WASM)",
                Mode: "Hybrid (browser + gateway)",
                State: readinessState,
                Detail: "Browser Silero v5 drives onset/barge-in debounce with RMS fallback; gateway Silero ONNX enforces authoritative speech framing."),
            new(
                Role: "Duplex Transport",
                Provider: "openconnect.voice.v2 (opt-in) / openconnect.voice.v1 (default)",
                Mode: deploymentMode,
                State: assistantConfigured ? readinessState : "Assistant gateway unconfigured",
                Detail: "32-byte binary PCM16 framing with monotonic gateway SpeechEpoch fencing and bounded rolling session budgets.")
        };

        return new LiveVoiceBenchmarkSnapshot(
            HasMeasuredRun: false,
            IsSyntheticHarness: false,
            SampleCount: 0,
            MeasuredAtUtc: null,
            DeploymentMode: deploymentMode,
            HardwareProfile: "Not yet recorded (declare CPU/GPU and network profile per run)",
            RunClassificationLabel: "No benchmark run recorded",
            UnderrunCount: null,
            QueuePressureCount: null,
            Metrics: CreateDefaultMetrics(),
            Stages: CreateDefaultStages(),
            Providers: providers);
    }

    public static IReadOnlyList<LiveVoiceMetricRow> CreateDefaultMetrics() =>
    [
        new(
            MetricName: "User speech end to first rendered response audio",
            TargetP50: "≤ 800 ms",
            TargetP95: "≤ 1,500 ms",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Warm single-session non-tool baseline; measured at AudioWorklet playback onset."),
        new(
            MetricName: "Clear end-of-turn detection",
            TargetP50: "≤ 400 ms",
            TargetP95: "—",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Gateway adaptive endpointing elapsed time from speech end."),
        new(
            MetricName: "Warm TTS first valid audio",
            TargetP50: "≤ 200 ms",
            TargetP95: "—",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Kokoro worker generation start to first validated 24 kHz PCM16 chunk."),
        new(
            MetricName: "Confirmed barge-in to local output stop",
            TargetP50: "—",
            TargetP95: "≤ 150 ms",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Immediate client playback clear without waiting for gateway round trip."),
        new(
            MetricName: "Stale audio frames after accepted interrupt",
            TargetP50: "0 frames",
            TargetP95: "0 frames",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Zero obsolete-epoch frames accepted or rendered after local suppression."),
        new(
            MetricName: "Duplicate turn admission",
            TargetP50: "0",
            TargetP95: "0",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Idempotent capture and epoch admission across retries and reconnects."),
        new(
            MetricName: "Continuous conversation longevity",
            TargetP50: "≥ 5 turns",
            TargetP95: "≥ 5 turns",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Consecutive turns on one microphone acquisition without tearing down audio."),
        new(
            MetricName: "Playback underruns",
            TargetP50: "0 audible gaps",
            TargetP95: "0 audible gaps",
            MeasuredP50: NotYetMeasured,
            MeasuredP95: NotYetMeasured,
            Notes: "Reported from playback worklet ring-buffer underrun counter.")
    ];

    public static IReadOnlyList<LiveVoiceStageRow> CreateDefaultStages() =>
    [
        new(
            StageName: "1. Endpointing",
            IllustrativeBudget: "250 ms",
            MeasuredLatency: NotYetMeasured,
            Detail: "Silero VAD + adaptive pause/hesitation stability before turn admission."),
        new(
            StageName: "2. STT finalization",
            IllustrativeBudget: "100 ms",
            MeasuredLatency: NotYetMeasured,
            Detail: "Cloud Muse ENDPOINTING final or local Whisper rolling-window reconciliation."),
        new(
            StageName: "3. Agent first speakable phrase",
            IllustrativeBudget: "200 ms",
            MeasuredLatency: NotYetMeasured,
            Detail: "Ordered assistant text deltas formatted into a coherent phrase segment."),
        new(
            StageName: "4. Policy screening",
            IllustrativeBudget: "Fail-closed gate",
            MeasuredLatency: NotYetMeasured,
            Detail: "Privacy firewall screens each complete phrase before synthesis; never bypassed."),
        new(
            StageName: "5. TTS onset",
            IllustrativeBudget: "200 ms",
            MeasuredLatency: NotYetMeasured,
            Detail: "Kokoro 82M incremental PCM16 first chunk generation (warm CPU baseline)."),
        new(
            StageName: "6. Transport & playback",
            IllustrativeBudget: "50 ms",
            MeasuredLatency: NotYetMeasured,
            Detail: "32-byte binary v2 frame delivery to browser AudioWorklet first rendered sample.")
    ];
}
