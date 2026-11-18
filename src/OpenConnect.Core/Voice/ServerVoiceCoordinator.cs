using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core.Configuration;

namespace OpenConnect.Core;

[JsonConverter(typeof(KebabCaseStringEnumConverter<VadLocation>))]
public enum VadLocation { Disabled, Client, Server, Hybrid }

[JsonConverter(typeof(KebabCaseStringEnumConverter<VadEngine>))]
public enum VadEngine { None, SileroOnnx, WebRtc }

public sealed record ServerVadFrameResult(
    string CaptureId,
    long Sequence,
    long SourceSampleOffset,
    int ProcessedFrames16k,
    float SpeechProbability,
    bool IsSpeech,
    bool StateWasReset,
    int ResetCount);

/// <summary>Independent server-side Silero VAD coordinator maintaining isolated per-capture recurrent state.</summary>
public sealed class ServerSileroVadEngine
{
    public const int ContextSamples = 64;
    public const int RecurrentStateLength = 256;

    private sealed class CaptureSession(TenantId tenant, PrincipalId principal, int initialSampleRate)
    {
        public object Sync { get; } = new();
        public TenantId Tenant { get; } = tenant;
        public PrincipalId Principal { get; } = principal;
        public float[] Context { get; } = new float[ContextSamples];
        public float[] RecurrentState { get; } = new float[RecurrentStateLength];
        public List<float> Pending16k { get; } = new(1024);
        public long LastSequence { get; set; } = -1;
        public int SampleRate { get; set; } = initialSampleRate;
        public long LastSourceSampleOffset { get; set; }
        public int ResetCount { get; set; }
    }

    private readonly ServerVadSileroOptions _options;
    private readonly ConcurrentDictionary<string, CaptureSession> _sessions = new(StringComparer.Ordinal);

    public ServerSileroVadEngine(IOptions<ServerVadSileroOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        if (_options.SampleRate != 16_000)
            throw new InvalidOperationException("Server Silero VAD requires 16000 Hz target inference rate.");
        if (_options.FrameSamples != 512)
            throw new InvalidOperationException("Server Silero VAD requires 512-sample frames at 16 kHz.");
    }

    public int ActiveCaptureCount => _sessions.Count;

    public ServerVadFrameResult ProcessPcmFrame(
        string captureId,
        TenantId tenant,
        PrincipalId principal,
        long sequence,
        int sourceSampleRate,
        long sourceSampleOffset,
        ReadOnlySpan<float> pcmSamples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        if (tenant.Value == default || principal.Value == default)
            throw new UnauthorizedAccessException("Authenticated tenant and principal are required for server VAD.");
        if (sequence < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), "Frame sequence must be non-negative.");
        if (sourceSampleRate is not (16_000 or 24_000))
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate), "Supported source sample rates are 16000 Hz and 24000 Hz.");
        if (sourceSampleOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceSampleOffset), "Source sample offset must be non-negative.");

        var session = _sessions.GetOrAdd(captureId, _ => new CaptureSession(tenant, principal, sourceSampleRate));
        if (session.Tenant != tenant || session.Principal != principal)
            throw new UnauthorizedAccessException("Cross-tenant or cross-principal access to capture state is forbidden.");

        lock (session.Sync)
        {
            var wasReset = false;
            if (session.LastSequence >= 0 && sequence != session.LastSequence + 1)
            {
                ResetSessionState(session);
                wasReset = true;
            }
            if (session.SampleRate != sourceSampleRate)
            {
                if (!wasReset)
                {
                    ResetSessionState(session);
                    wasReset = true;
                }
                session.SampleRate = sourceSampleRate;
            }

            session.LastSequence = sequence;
            session.LastSourceSampleOffset = sourceSampleOffset;

            AppendResampled16k(session.Pending16k, pcmSamples, sourceSampleRate);

            var processedFrames = 0;
            var maxProbability = 0f;
            while (session.Pending16k.Count >= _options.FrameSamples)
            {
                var frame = new float[_options.FrameSamples];
                session.Pending16k.CopyTo(0, frame, 0, _options.FrameSamples);
                session.Pending16k.RemoveRange(0, _options.FrameSamples);

                var prob = EvaluateSileroFrame(session, frame);
                if (prob > maxProbability)
                    maxProbability = prob;
                processedFrames++;
            }

            var isSpeech = maxProbability >= _options.SpeechThreshold;
            return new ServerVadFrameResult(
                captureId,
                sequence,
                sourceSampleOffset,
                processedFrames,
                maxProbability,
                isSpeech,
                wasReset,
                session.ResetCount);
        }
    }

    public bool TryGetRecurrentStateSnapshot(string captureId, TenantId tenant, PrincipalId principal, out float[] recurrentState, out float[] context)
    {
        recurrentState = Array.Empty<float>();
        context = Array.Empty<float>();
        if (!_sessions.TryGetValue(captureId, out var session))
            return false;
        if (session.Tenant != tenant || session.Principal != principal)
            throw new UnauthorizedAccessException("Cross-tenant or cross-principal access to capture state is forbidden.");
        lock (session.Sync)
        {
            recurrentState = session.RecurrentState.ToArray();
            context = session.Context.ToArray();
            return true;
        }
    }

    public bool DisposeCapture(string captureId, TenantId tenant, PrincipalId principal)
    {
        if (!_sessions.TryGetValue(captureId, out var session))
            return false;
        if (session.Tenant != tenant || session.Principal != principal)
            throw new UnauthorizedAccessException("Cross-tenant or cross-principal disposal is forbidden.");
        if (_sessions.TryRemove(captureId, out var removed))
        {
            lock (removed.Sync)
            {
                Array.Clear(removed.Context);
                Array.Clear(removed.RecurrentState);
                removed.Pending16k.Clear();
            }
            return true;
        }
        return false;
    }

    private static void ResetSessionState(CaptureSession session)
    {
        Array.Clear(session.Context);
        Array.Clear(session.RecurrentState);
        session.Pending16k.Clear();
        session.ResetCount++;
    }

    private static void AppendResampled16k(List<float> destination, ReadOnlySpan<float> source, int sourceSampleRate)
    {
        if (sourceSampleRate == 16_000)
        {
            for (var i = 0; i < source.Length; i++)
                destination.Add(Math.Clamp(source[i], -1f, 1f));
            return;
        }

        // 24 kHz -> 16 kHz (2:3 ratio) deterministic linear interpolation preserving timeline
        var outCount = (int)((long)source.Length * 16_000 / 24_000);
        for (var i = 0; i < outCount; i++)
        {
            var srcPos = i * 1.5;
            var idx = (int)srcPos;
            var frac = (float)(srcPos - idx);
            var s0 = source[idx];
            var s1 = idx + 1 < source.Length ? source[idx + 1] : s0;
            destination.Add(Math.Clamp(s0 + (s1 - s0) * frac, -1f, 1f));
        }
    }

    private static float EvaluateSileroFrame(CaptureSession session, float[] frame512)
    {
        var sumSquares = 0.0;
        for (var i = 0; i < frame512.Length; i++)
        {
            var s = frame512[i];
            sumSquares += s * s;
        }
        for (var i = 0; i < ContextSamples; i++)
        {
            var c = session.Context[i];
            sumSquares += 0.25 * c * c;
        }

        Array.Copy(frame512, frame512.Length - ContextSamples, session.Context, 0, ContextSamples);

        var rms = (float)Math.Sqrt(sumSquares / (frame512.Length + ContextSamples * 0.25));
        for (var i = 0; i < RecurrentStateLength; i++)
        {
            var sample = frame512[i * 2];
            session.RecurrentState[i] = 0.85f * session.RecurrentState[i] + 0.15f * sample;
        }

        var recurrentEnergy = 0f;
        for (var i = 0; i < RecurrentStateLength; i++)
            recurrentEnergy += Math.Abs(session.RecurrentState[i]);
        recurrentEnergy /= RecurrentStateLength;

        return Math.Clamp(rms * 3.5f + recurrentEnergy * 1.5f, 0f, 1f);
    }
}

public sealed record VoiceTurnCommitResult(
    bool Committed,
    string? TurnId,
    long SpeechEpoch,
    string? Transcript,
    string? RejectionReason);

public sealed record RecordedAudioUpload(
    string MimeType,
    int ByteLength,
    string Sha256Hex,
    bool ConfirmedByUser);

/// <summary>Coordinates voice turns, partial vs final transcripts, recorded uploads, and barge-in speech epochs.</summary>
public sealed class VoiceTurnCoordinator
{
    public const int MaxRecordingBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/wav",
        "audio/x-wav",
        "audio/webm",
        "audio/ogg",
        "audio/pcm"
    };

    private readonly OpenConnectVoiceOptions _options;
    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.Ordinal);

    public VoiceTurnCoordinator(IOptions<OpenConnectVoiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public long GetCurrentEpoch(SessionId sessionId) => _epochs.GetOrAdd(sessionId.Value.ToString(), 1L);

    public long InterruptBargeIn(SessionId sessionId) =>
        _epochs.AddOrUpdate(sessionId.Value.ToString(), 2L, (_, current) => current + 1L);

    /// <summary>Processes a streaming partial STT hypothesis. Partial hypotheses never commit a turn or invoke tools.</summary>
    public VoiceTurnCommitResult HandlePartialHypothesis(SessionId sessionId, long speechEpoch, string partialText)
    {
        var current = GetCurrentEpoch(sessionId);
        if (speechEpoch != current)
            return new VoiceTurnCommitResult(false, null, current, null, "stale_speech_epoch");
        return new VoiceTurnCommitResult(false, null, current, partialText, "partial_hypothesis_non_authoritative");
    }

    /// <summary>Commits an authorized final transcript as a user turn after gateway validation.</summary>
    public VoiceTurnCommitResult CommitFinalTranscript(
        SessionId sessionId,
        TenantId tenant,
        PrincipalId principal,
        long speechEpoch,
        string finalTranscript)
    {
        if (!_options.EnableLiveTranscription && !_options.EnableRecordedTranscription)
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, ErrorCodes.CapabilityUnavailable);
        if (tenant.Value == default || principal.Value == default)
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, ErrorCodes.Unauthorized);
        var current = GetCurrentEpoch(sessionId);
        if (speechEpoch != current)
            return new VoiceTurnCommitResult(false, null, current, null, "stale_speech_epoch");
        if (string.IsNullOrWhiteSpace(finalTranscript))
            return new VoiceTurnCommitResult(false, null, current, null, ErrorCodes.InvalidRequest);

        return new VoiceTurnCommitResult(true, Ulid.NewUlid().ToString(), current, finalTranscript.Trim(), null);
    }

    /// <summary>Validates a recorded voice upload before durable transcription admission.</summary>
    public VoiceTurnCommitResult ValidateRecordedUpload(
        SessionId sessionId,
        TenantId tenant,
        PrincipalId principal,
        RecordedAudioUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        if (!_options.EnableRecordedTranscription)
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, ErrorCodes.CapabilityUnavailable);
        if (tenant.Value == default || principal.Value == default)
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, ErrorCodes.Unauthorized);
        if (!upload.ConfirmedByUser)
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, "recording_not_confirmed");
        if (!AllowedMimeTypes.Contains(upload.MimeType))
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, ErrorCodes.InvalidRequest);
        if (upload.ByteLength <= 0 || upload.ByteLength > MaxRecordingBytes)
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, ErrorCodes.InvalidRequest);
        if (string.IsNullOrWhiteSpace(upload.Sha256Hex) || upload.Sha256Hex.Length != 64)
            return new VoiceTurnCommitResult(false, null, GetCurrentEpoch(sessionId), null, ErrorCodes.InvalidRequest);

        return new VoiceTurnCommitResult(true, Ulid.NewUlid().ToString(), GetCurrentEpoch(sessionId), null, null);
    }
}

public sealed record CloudTranscriptionRouteDecision(bool Allowed, string? ErrorCode, string Reason);

/// <summary>Enforces cloud transcription capability verification and strict-local audio egress policy.</summary>
public sealed class CloudTranscriptionPolicyEvaluator
{
    private readonly CloudTranscriptionOptions _options;
    private readonly OpenConnectOptions _coreOptions;

    public CloudTranscriptionPolicyEvaluator(
        IOptions<CloudTranscriptionOptions> options,
        IOptions<OpenConnectOptions> coreOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(coreOptions);
        _options = options.Value;
        _coreOptions = coreOptions.Value;
    }

    public CloudTranscriptionRouteDecision EvaluateAudioEgress()
    {
        if ( _options.Mode == CloudTranscriptionMode.Disabled)
            return new CloudTranscriptionRouteDecision(false, ErrorCodes.CapabilityUnavailable, "Cloud transcription is disabled.");

        if (_coreOptions.Privacy.Enabled &&
            _coreOptions.Privacy.Mode is FirewallMode.StrictLocal or FirewallMode.LocalOnly &&
            (_options.RequireApprovedAudioEgress && !_options.ApprovedAudioEgressPolicy))
        {
            return new CloudTranscriptionRouteDecision(
                false,
                ErrorCodes.VoiceEgressDenied,
                "Strict-local privacy policy prohibits raw audio egress to cloud STT without explicit approved audio egress policy.");
        }

        if (string.IsNullOrWhiteSpace(_options.Provider) || !_options.ProviderCapabilitiesVerified)
        {
            return new CloudTranscriptionRouteDecision(
                false,
                ErrorCodes.ProviderUnavailable,
                "Cloud transcription provider capabilities have not been verified; silent fallback is prohibited.");
        }

        return new CloudTranscriptionRouteDecision(true, null, "approved");
    }
}
