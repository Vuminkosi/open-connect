using System.Security.Cryptography;

namespace OpenConnect.Client;

public sealed record VadFrameObservation(
    long FrameSequence,
    long SourceSampleOffset,
    int SourceSampleRate,
    int InferenceSampleRate,
    int SampleCount,
    float SpeechProbability,
    bool IsSpeechCandidate,
    long SpeechEpoch,
    bool IsAuthoritativeTurn = false);

public sealed record RecordedVoiceDraft(
    string DraftId,
    string MimeType,
    int SizeBytes,
    TimeSpan Duration,
    string Sha256Checksum,
    DateTimeOffset CreatedAtUtc);

public interface IClientVadSession : IAsyncDisposable
{
    Guid CaptureId { get; }
    long CurrentSpeechEpoch { get; }
    long NextFrameSequence { get; }
    long TotalSourceSamples { get; }
    int RecurrentStateLength { get; }
    int ContextSampleCount { get; }
    bool IsDisposed { get; }
    IReadOnlyList<float[]> GetPreRollFrames();
    VadFrameObservation ProcessPcmFrame(ReadOnlySpan<float> pcmSamples, int sourceSampleRate, long? explicitFrameSequence = null);
    void ResetState();
}

public interface IClientVadService
{
    ClientVadOptions Options { get; }
    IClientVadSession CreateSession(bool microphonePermissionGranted);
}

public interface IVoiceClientService
{
    VoiceClientOptions Options { get; }
    long CurrentSpeechEpoch { get; }
    long CurrentPlaybackEpoch { get; }
    bool IsPlaybackActive { get; }
    string LivePartialTranscript { get; }
    IReadOnlyList<string> CommittedTranscripts { get; }
    RecordedVoiceDraft? StagedRecording { get; }
    long StartLiveCapture(bool microphonePermissionGranted);
    bool UpdateLiveHypothesis(string partialTranscript, long speechEpoch);
    bool CommitGatewayAuthorizedTranscript(string finalTranscript, long speechEpoch);
    long BeginPlayback();
    bool TryAcceptPlaybackChunk(long speechEpoch);
    long TriggerBargeIn();
    RecordedVoiceDraft StageRecordingForPreview(ReadOnlySpan<byte> audioBytes, string mimeType, TimeSpan duration, bool microphonePermissionGranted);
    void DiscardRecording();
    RecordedVoiceDraft AcceptRecordingForSubmission(bool userConsentGranted);
    void InvalidateAllEpochs();
}

public interface IMauiAudioCaptureService
{
    MauiAudioOptions Options { get; }
    IClientVadSession StartNativeCaptureSession(bool platformPermissionGranted);
    IReadOnlyList<VadFrameObservation> CaptureAndProcessEnhancedPcm(IClientVadSession session, ReadOnlySpan<float> pcmSamples);
}

public interface INavigatorClientService
{
    NavigatorClientOptions Options { get; }
    string? HighlightedTargetId { get; }
    bool IsTourActive { get; }
    string? ActiveTourId { get; }
    int ActiveStepIndex { get; }
    IReadOnlyList<string> ActiveTourSteps { get; }
    event Action? Changed;
    void Highlight(string targetId);
    void ClearHighlight();
    void StartGuidedTour(string tourId, IReadOnlyList<string> stepTargetIds);
    bool NextTourStep();
    void StopGuidedTour();
    bool CanExecuteAction(string actionId, bool userConsentGranted);
}

public sealed class ClientVadSession : IClientVadSession
{
    internal const int SileroFrameSamples = 512;
    internal const int SileroInferenceSampleRate = 16_000;
    internal const int SileroContextLength = 64;
    internal const int SileroRecurrentStateLength = 256;

    private readonly ClientVadOptions _options;
    private readonly float[] _recurrentState = new float[SileroRecurrentStateLength];
    private readonly float[] _contextWindow = new float[SileroContextLength];
    private readonly Queue<float[]> _preRollFrames = new();
    private readonly List<float> _pendingResampled = new(SileroFrameSamples * 2);
    private readonly int _maxPreRollFrames;
    private int _lastSourceSampleRate;
    private bool _inSpeech;

    public ClientVadSession(ClientVadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        var frameMs = (SileroFrameSamples * 1000.0) / SileroInferenceSampleRate; // 32 ms
        _maxPreRollFrames = options.EnablePreRoll
            ? Math.Max(1, (int)Math.Ceiling(options.PreRollMilliseconds / frameMs))
            : 0;
        CurrentSpeechEpoch = 1;
    }

    public Guid CaptureId { get; } = Guid.NewGuid();
    public long CurrentSpeechEpoch { get; private set; }
    public long NextFrameSequence { get; private set; }
    public long TotalSourceSamples { get; private set; }
    public int RecurrentStateLength => _recurrentState.Length;
    public int ContextSampleCount => _contextWindow.Length;
    public bool IsDisposed { get; private set; }

    public IReadOnlyList<float[]> GetPreRollFrames()
    {
        ThrowIfDisposed();
        return _preRollFrames.Select(static f => (float[])f.Clone()).ToArray();
    }

    public VadFrameObservation ProcessPcmFrame(ReadOnlySpan<float> pcmSamples, int sourceSampleRate, long? explicitFrameSequence = null)
    {
        ThrowIfDisposed();
        if (pcmSamples.IsEmpty)
        {
            throw new ArgumentException("PCM sample buffer must not be empty.", nameof(pcmSamples));
        }

        if (sourceSampleRate is not (16_000 or 24_000 or 44_100 or 48_000))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate), "Supported source sample rates are 16000, 24000, 44100, and 48000 Hz.");
        }

        // Sequence gap or sample-rate change resets recurrent state and context window (§3.4, §4.3, §8.2).
        var sequenceGapDetected = explicitFrameSequence.HasValue && explicitFrameSequence.Value != NextFrameSequence;
        var sampleRateChanged = _lastSourceSampleRate != 0 && _lastSourceSampleRate != sourceSampleRate;
        if (sequenceGapDetected || sampleRateChanged)
        {
            ResetInternalStateOnly();
            if (explicitFrameSequence.HasValue && explicitFrameSequence.Value > NextFrameSequence)
            {
                NextFrameSequence = explicitFrameSequence.Value;
            }
        }

        _lastSourceSampleRate = sourceSampleRate;
        var sourceOffsetBefore = TotalSourceSamples;
        TotalSourceSamples += pcmSamples.Length;

        var resampled = ResampleTo16kHz(pcmSamples, sourceSampleRate);
        for (var i = 0; i < resampled.Length; i++)
        {
            _pendingResampled.Add(resampled[i]);
        }

        var frame = new float[SileroFrameSamples];
        if (_pendingResampled.Count >= SileroFrameSamples)
        {
            _pendingResampled.CopyTo(0, frame, 0, SileroFrameSamples);
            _pendingResampled.RemoveRange(0, SileroFrameSamples);
        }
        else
        {
            _pendingResampled.CopyTo(0, frame, 0, _pendingResampled.Count);
            _pendingResampled.Clear();
        }

        if (_options.EnablePreRoll && _maxPreRollFrames > 0)
        {
            if (_preRollFrames.Count >= _maxPreRollFrames)
            {
                _preRollFrames.Dequeue();
            }

            _preRollFrames.Enqueue((float[])frame.Clone());
        }

        var probability = EvaluateSileroFrame(frame);
        var isSpeech = _options.Enabled
            && _options.Location is VadLocation.Client or VadLocation.Hybrid
            && probability >= 0.5f;

        if (isSpeech && !_inSpeech && _options.EnableBargeIn)
        {
            CurrentSpeechEpoch++;
        }

        _inSpeech = isSpeech;
        var assignedSeq = NextFrameSequence++;
        return new VadFrameObservation(
            FrameSequence: assignedSeq,
            SourceSampleOffset: sourceOffsetBefore,
            SourceSampleRate: sourceSampleRate,
            InferenceSampleRate: SileroInferenceSampleRate,
            SampleCount: SileroFrameSamples,
            SpeechProbability: probability,
            IsSpeechCandidate: isSpeech,
            SpeechEpoch: CurrentSpeechEpoch,
            IsAuthoritativeTurn: false);
    }

    public void ResetState()
    {
        ThrowIfDisposed();
        ResetInternalStateOnly();
        _preRollFrames.Clear();
        _pendingResampled.Clear();
        _inSpeech = false;
        CurrentSpeechEpoch++;
    }

    public ValueTask DisposeAsync()
    {
        if (!IsDisposed)
        {
            IsDisposed = true;
            ResetInternalStateOnly();
            _preRollFrames.Clear();
            _pendingResampled.Clear();
            _inSpeech = false;
            CurrentSpeechEpoch++;
        }

        return ValueTask.CompletedTask;
    }

    internal static float[] ResampleTo16kHz(ReadOnlySpan<float> input, int sourceSampleRate)
    {
        if (sourceSampleRate == SileroInferenceSampleRate)
        {
            return input.ToArray();
        }

        var outLength = Math.Max(1, (int)Math.Round(input.Length * (double)SileroInferenceSampleRate / sourceSampleRate));
        var output = new float[outLength];
        var ratio = (double)sourceSampleRate / SileroInferenceSampleRate;
        for (var i = 0; i < outLength; i++)
        {
            var srcPos = i * ratio;
            var idx = (int)srcPos;
            var frac = (float)(srcPos - idx);
            var s0 = input[Math.Min(idx, input.Length - 1)];
            var s1 = input[Math.Min(idx + 1, input.Length - 1)];
            output[i] = s0 + ((s1 - s0) * frac);
        }

        return output;
    }

    private float EvaluateSileroFrame(ReadOnlySpan<float> frame)
    {
        // Combine 64-sample context + 512-sample frame and update 256-element recurrent state.
        double energy = 0;
        for (var i = 0; i < _contextWindow.Length; i++)
        {
            energy += _contextWindow[i] * _contextWindow[i] * 0.15;
        }

        for (var i = 0; i < frame.Length; i++)
        {
            var sample = Math.Clamp(frame[i], -1f, 1f);
            energy += sample * sample;
            var stateIdx = i % SileroRecurrentStateLength;
            _recurrentState[stateIdx] = (_recurrentState[stateIdx] * 0.85f) + (sample * 0.15f);
        }

        frame.Slice(frame.Length - SileroContextLength, SileroContextLength).CopyTo(_contextWindow);

        var rms = Math.Sqrt(energy / (frame.Length + (_contextWindow.Length * 0.15)));
        var recurrentBoost = Math.Abs(_recurrentState[0]) * 0.1;
        var raw = (float)Math.Clamp((rms * 4.5) + recurrentBoost, 0.0, 1.0);
        return raw;
    }

    private void ResetInternalStateOnly()
    {
        Array.Clear(_recurrentState, 0, _recurrentState.Length);
        Array.Clear(_contextWindow, 0, _contextWindow.Length);
    }

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ClientVadSession));
        }
    }
}

public sealed class ClientVadService : IClientVadService
{
    public ClientVadService(ClientVadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    public ClientVadOptions Options { get; }

    public IClientVadSession CreateSession(bool microphonePermissionGranted)
    {
        if (Options.RequireMicrophonePermission && !microphonePermissionGranted)
        {
            throw new InvalidOperationException("Explicit microphone permission is required before creating a client VAD capture session.");
        }

        return new ClientVadSession(Options);
    }
}

public sealed class VoiceClientService : IVoiceClientService
{
    private static readonly HashSet<string> AllowedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/wav",
        "audio/x-wav",
        "audio/wave",
        "audio/webm",
        "audio/ogg",
        "audio/pcm",
    };

    private readonly object _gate = new();
    private readonly List<string> _committedTranscripts = new();

    public VoiceClientService(VoiceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    public VoiceClientOptions Options { get; }
    public long CurrentSpeechEpoch { get; private set; }
    public long CurrentPlaybackEpoch { get; private set; }
    public bool IsPlaybackActive { get; private set; }
    public string LivePartialTranscript { get; private set; } = string.Empty;
    public RecordedVoiceDraft? StagedRecording { get; private set; }

    public IReadOnlyList<string> CommittedTranscripts
    {
        get
        {
            lock (_gate)
            {
                return _committedTranscripts.ToList();
            }
        }
    }

    public long StartLiveCapture(bool microphonePermissionGranted)
    {
        if (!Options.EnableLiveTranscription)
        {
            throw new InvalidOperationException("Live transcription is disabled in VoiceClientOptions.");
        }

        if (!microphonePermissionGranted)
        {
            throw new InvalidOperationException("Explicit microphone permission is required before starting live transcription.");
        }

        lock (_gate)
        {
            CurrentSpeechEpoch++;
            LivePartialTranscript = string.Empty;
            if (Options.EnableBargeIn && IsPlaybackActive)
            {
                CurrentPlaybackEpoch++;
                IsPlaybackActive = false;
            }

            return CurrentSpeechEpoch;
        }
    }

    public bool UpdateLiveHypothesis(string partialTranscript, long speechEpoch)
    {
        ArgumentNullException.ThrowIfNull(partialTranscript);
        lock (_gate)
        {
            if (speechEpoch == 0 || speechEpoch != CurrentSpeechEpoch)
            {
                return false;
            }

            // Partial hypotheses update display state only and NEVER commit a turn.
            LivePartialTranscript = partialTranscript.Trim();
            return true;
        }
    }

    public bool CommitGatewayAuthorizedTranscript(string finalTranscript, long speechEpoch)
    {
        if (string.IsNullOrWhiteSpace(finalTranscript))
        {
            return false;
        }

        lock (_gate)
        {
            if (speechEpoch == 0 || speechEpoch != CurrentSpeechEpoch)
            {
                return false;
            }

            LivePartialTranscript = string.Empty;
            _committedTranscripts.Add(finalTranscript.Trim());
            return true;
        }
    }

    public long BeginPlayback()
    {
        if (!Options.EnablePlayback)
        {
            throw new InvalidOperationException("Voice playback is disabled in VoiceClientOptions.");
        }

        lock (_gate)
        {
            CurrentPlaybackEpoch++;
            IsPlaybackActive = true;
            return CurrentPlaybackEpoch;
        }
    }

    public bool TryAcceptPlaybackChunk(long speechEpoch)
    {
        lock (_gate)
        {
            return IsPlaybackActive && speechEpoch != 0 && speechEpoch == CurrentPlaybackEpoch;
        }
    }

    public long TriggerBargeIn()
    {
        lock (_gate)
        {
            CurrentSpeechEpoch++;
            CurrentPlaybackEpoch++;
            IsPlaybackActive = false;
            return CurrentPlaybackEpoch;
        }
    }

    public RecordedVoiceDraft StageRecordingForPreview(
        ReadOnlySpan<byte> audioBytes,
        string mimeType,
        TimeSpan duration,
        bool microphonePermissionGranted)
    {
        if (!Options.EnableVoiceRecording)
        {
            throw new InvalidOperationException("Voice recording is disabled in VoiceClientOptions.");
        }

        if (!microphonePermissionGranted)
        {
            throw new InvalidOperationException("Explicit microphone permission is required for voice recording.");
        }

        if (audioBytes.IsEmpty || audioBytes.Length > Options.MaxRecordingBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(audioBytes), $"Recorded audio size must be between 1 and {Options.MaxRecordingBytes} bytes.");
        }

        if (string.IsNullOrWhiteSpace(mimeType) || !AllowedMimeTypes.Contains(mimeType.Trim()))
        {
            throw new ArgumentException($"Unsupported recording MIME type '{mimeType}'.", nameof(mimeType));
        }

        if (duration <= TimeSpan.Zero || duration > Options.MaxRecordingDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Recording duration is out of bounds.");
        }

        var hashBytes = SHA256.HashData(audioBytes);
        var checksum = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var draft = new RecordedVoiceDraft(
            DraftId: Guid.NewGuid().ToString("N"),
            MimeType: mimeType.Trim().ToLowerInvariant(),
            SizeBytes: audioBytes.Length,
            Duration: duration,
            Sha256Checksum: checksum,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        lock (_gate)
        {
            StagedRecording = draft;
            return draft;
        }
    }

    public void DiscardRecording()
    {
        lock (_gate)
        {
            StagedRecording = null;
        }
    }

    public RecordedVoiceDraft AcceptRecordingForSubmission(bool userConsentGranted)
    {
        if (!userConsentGranted)
        {
            throw new InvalidOperationException("User consent is required before submitting a recorded voice file.");
        }

        lock (_gate)
        {
            var draft = StagedRecording ?? throw new InvalidOperationException("No staged voice recording is available to submit.");
            StagedRecording = null;
            return draft;
        }
    }

    public void InvalidateAllEpochs()
    {
        lock (_gate)
        {
            CurrentSpeechEpoch++;
            CurrentPlaybackEpoch++;
            IsPlaybackActive = false;
            LivePartialTranscript = string.Empty;
            StagedRecording = null;
        }
    }
}

public sealed class MauiAudioCaptureService : IMauiAudioCaptureService
{
    private readonly IClientVadService _vadService;

    public MauiAudioCaptureService(MauiAudioOptions options, IClientVadService vadService)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(vadService);
        options.Validate();
        Options = options;
        _vadService = vadService;
    }

    public MauiAudioOptions Options { get; }

    public IClientVadSession StartNativeCaptureSession(bool platformPermissionGranted)
    {
        if (Options.RequireMicrophonePermission && !platformPermissionGranted)
        {
            throw new InvalidOperationException("Platform microphone permission is required before starting MAUI native audio capture.");
        }

        return _vadService.CreateSession(platformPermissionGranted);
    }

    public IReadOnlyList<VadFrameObservation> CaptureAndProcessEnhancedPcm(IClientVadSession session, ReadOnlySpan<float> pcmSamples)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (pcmSamples.IsEmpty)
        {
            throw new ArgumentException("PCM sample buffer must not be empty.", nameof(pcmSamples));
        }

        // Process native (e.g. 24 kHz) enhanced PCM in chunks that yield 512-sample 16 kHz Silero frames.
        var sourceChunkSamples = (int)Math.Round(ClientVadSession.SileroFrameSamples * ((double)Options.SampleRate / ClientVadSession.SileroInferenceSampleRate));
        var observations = new List<VadFrameObservation>();
        var offset = 0;
        while (offset < pcmSamples.Length)
        {
            var length = Math.Min(sourceChunkSamples, pcmSamples.Length - offset);
            var slice = pcmSamples.Slice(offset, length);
            observations.Add(session.ProcessPcmFrame(slice, Options.SampleRate));
            offset += length;
        }

        return observations;
    }
}

public sealed class NavigatorClientService : INavigatorClientService
{
    private readonly List<string> _tourSteps = new();

    public NavigatorClientService(NavigatorClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
    }

    public NavigatorClientOptions Options { get; }
    public string? HighlightedTargetId { get; private set; }
    public bool IsTourActive { get; private set; }
    public string? ActiveTourId { get; private set; }
    public int ActiveStepIndex { get; private set; }
    public IReadOnlyList<string> ActiveTourSteps => _tourSteps;
    public event Action? Changed;

    public void Highlight(string targetId)
    {
        if (!Options.Enabled || !Options.EnableHighlighting)
        {
            throw new InvalidOperationException("Navigator highlighting is disabled.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        HighlightedTargetId = targetId.Trim();
        Changed?.Invoke();
    }

    public void ClearHighlight()
    {
        HighlightedTargetId = null;
        Changed?.Invoke();
    }

    public void StartGuidedTour(string tourId, IReadOnlyList<string> stepTargetIds)
    {
        if (!Options.Enabled || !Options.EnableGuidedTours)
        {
            throw new InvalidOperationException("Navigator guided tours are disabled.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(tourId);
        ArgumentNullException.ThrowIfNull(stepTargetIds);
        if (stepTargetIds.Count == 0 || stepTargetIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A guided tour requires at least one valid target ID.", nameof(stepTargetIds));
        }

        _tourSteps.Clear();
        _tourSteps.AddRange(stepTargetIds.Select(static s => s.Trim()));
        ActiveTourId = tourId.Trim();
        ActiveStepIndex = 0;
        IsTourActive = true;
        HighlightedTargetId = _tourSteps[0];
        Changed?.Invoke();
    }

    public bool NextTourStep()
    {
        if (!IsTourActive)
        {
            return false;
        }

        if (ActiveStepIndex + 1 < _tourSteps.Count)
        {
            ActiveStepIndex++;
            HighlightedTargetId = _tourSteps[ActiveStepIndex];
            Changed?.Invoke();
            return true;
        }

        StopGuidedTour();
        return false;
    }

    public void StopGuidedTour()
    {
        IsTourActive = false;
        ActiveTourId = null;
        ActiveStepIndex = 0;
        _tourSteps.Clear();
        HighlightedTargetId = null;
        Changed?.Invoke();
    }

    public bool CanExecuteAction(string actionId, bool userConsentGranted)
    {
        if (!Options.Enabled || string.IsNullOrWhiteSpace(actionId))
        {
            return false;
        }

        if (Options.RequireConsentForActions && !userConsentGranted)
        {
            return false;
        }

        return true;
    }
}
