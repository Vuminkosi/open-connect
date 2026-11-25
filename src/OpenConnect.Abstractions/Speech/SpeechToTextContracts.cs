namespace OpenConnect.Abstractions.Speech;

/// <summary>Finalized transcript turn emitted by a realtime speech-to-text session.</summary>
public sealed record RealtimeTranscriptTurn(long? TurnId, string Text);

/// <summary>Non-authoritative partial transcript emitted while speech is in progress.</summary>
public sealed record RealtimePartialTranscript(long? TurnId, string Text);

/// <summary>Realtime speech-to-text session result containing the provider session identifier and finalized turns.</summary>
public sealed record RealtimeTranscriptionResult(string SessionId, IReadOnlyList<RealtimeTranscriptTurn> Turns)
{
    /// <summary>Ordered final transcript text values for each finalized turn.</summary>
    public IReadOnlyList<string> FinalTranscripts => Turns.Select(turn => turn.Text).ToArray();
}

/// <summary>Single diarized or segmented turn from a whole-recording speech-to-text upload.</summary>
public sealed record SpeechToTextTurn(int TurnId, long StartMs, long EndMs, string Transcript, string Speaker);

/// <summary>Whole-recording speech-to-text upload result.</summary>
public sealed record SpeechToTextUploadResult(
    string SessionId,
    string Transcript,
    long AudioDurationMs,
    IReadOnlyList<SpeechToTextTurn> Turns);

/// <summary>Streams 24 kHz mono 16-bit PCM audio to a realtime speech-to-text provider.</summary>
public interface IRealtimeSpeechToTextClient
{
    /// <summary>Gets a value indicating whether a realtime transcription route and credential are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Transcribes a 24 kHz mono 16-bit PCM stream until completion.</summary>
    Task<RealtimeTranscriptionResult> TranscribePcmAsync(
        Stream pcm24kMono16Bit,
        CancellationToken cancellationToken = default,
        Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
        Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null);
}

/// <summary>Uploads a complete audio recording to a speech-to-text provider.</summary>
public interface ISpeechToTextUploadClient
{
    /// <summary>Gets a value indicating whether an upload transcription route and credential are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Uploads an audio recording and returns the provider transcription result.</summary>
    Task<SpeechToTextUploadResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default);
}

/// <summary>One ordered mono floating-point audio frame for server-side voice activity detection.</summary>
public sealed record VoiceActivityFrame(
    long Sequence,
    int SampleRate,
    ReadOnlyMemory<float> Samples);

/// <summary>Acoustic speech activity estimate for a single audio frame.</summary>
public sealed record VoiceActivityResult(
    long Sequence,
    float SpeechProbability,
    bool IsSpeech,
    bool StateWasReset,
    long AudioStartSample = -1,
    long AudioEndSample = -1);

/// <summary>Creates isolated recurrent inference state for each voice stream.</summary>
public interface IVoiceActivityDetector
{
    /// <summary>Creates a new stateful voice activity detection session.</summary>
    IVoiceActivityDetectionSession CreateSession();
}

/// <summary>Stateful per-stream voice activity detection session.</summary>
public interface IVoiceActivityDetectionSession : IDisposable
{
    /// <summary>Evaluates a 16 kHz mono 512-sample audio frame for speech activity.</summary>
    ValueTask<VoiceActivityResult> AnalyzeAsync(
        VoiceActivityFrame frame,
        CancellationToken cancellationToken = default);

    /// <summary>Resets the recurrent state and context buffer for this session.</summary>
    void Reset();
}
