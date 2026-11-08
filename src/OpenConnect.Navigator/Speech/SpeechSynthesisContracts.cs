namespace OpenConnect.Navigator;

/// <summary>Privacy-safe reference-based request; text itself must remain outside events and telemetry.</summary>
public sealed record SpeechSynthesisRequest(
    string RequestId,
    string TextReference,
    string Locale,
    string VoiceId,
    string Format,
    long SpeechEpoch,
    DateTimeOffset Deadline);

public enum SpeechSynthesisEventKind { Started, AudioChunk, Completed, Error }

/// <summary>Metadata-only synthesis event. Audio bytes are delivered out of band and never stored here.</summary>
public sealed record SpeechSynthesisEvent(
    SpeechSynthesisEventKind Kind,
    string RequestId,
    long SpeechEpoch,
    int? ChunkIndex = null,
    SpeechError? Error = null);

public enum SpeechErrorCode
{
    SpeechModelUnavailable,
    License,
    Capacity,
    Cancelled
}

public sealed record SpeechError(SpeechErrorCode Code);
