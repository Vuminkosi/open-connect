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
