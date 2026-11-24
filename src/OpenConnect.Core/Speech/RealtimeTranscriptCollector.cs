using System.Text.Json;
using OpenConnect.Abstractions.Speech;

namespace OpenConnect.Core.Speech;

/// <summary>Extracts partial and provider-final utterances from realtime speech-to-text JSON events.
/// <c>PUSH_TO_TALK</c> completes with <c>transcript</c>/<c>final:true</c>; <c>ENDPOINTING</c> and
/// <c>DIARIZATION</c> complete with <c>speechComplete</c>/<c>turnId</c>.</summary>
public sealed class RealtimeTranscriptCollector
{
    private const int MaxPartialTranscriptLength = 8_000;
    private readonly List<RealtimeTranscriptTurn> _transcripts = [];
    private readonly HashSet<long> _completedTurnIds = [];

    /// <summary>Highest monotonically increasing partial transcript revision observed so far.</summary>
    public long LastPartialRevision { get; private set; }

    /// <summary>Resets partial transcript revision tracking for a new utterance capture.</summary>
    public void ResetPartialRevision() => LastPartialRevision = 0;

    /// <summary>Ordered finalized transcript turns collected so far.</summary>
    public IReadOnlyList<RealtimeTranscriptTurn> Turns => _transcripts
        .OrderBy(turn => turn.TurnId ?? long.MaxValue)
        .ToArray();

    /// <summary>Ordered finalized transcript text values collected so far.</summary>
    public IReadOnlyList<string> Transcripts => Turns.Select(turn => turn.Text).ToArray();

    /// <summary>Attempts to read a bounded non-final partial transcript from a provider JSON event.</summary>
    public bool TryReadPartial(string json, out RealtimePartialTranscript? partial)
        => TryReadPartial(json, out partial, out _);

    /// <summary>Attempts to read a bounded non-final partial transcript and its monotonic revision from a provider JSON event.</summary>
    public bool TryReadPartial(string json, out RealtimePartialTranscript? partial, out long revision)
    {
        partial = null;
        revision = LastPartialRevision;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeValue)
                || typeValue.ValueKind != JsonValueKind.String
                || !string.Equals(typeValue.GetString(), "transcript", StringComparison.Ordinal)
                || !root.TryGetProperty("final", out var finalValue)
                || finalValue.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("transcript", out var transcriptValue)
                || transcriptValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var transcript = transcriptValue.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > MaxPartialTranscriptLength)
            {
                return false;
            }

            long? turnId = null;
            if (root.TryGetProperty("turnId", out var turnValue))
            {
                if (turnValue.ValueKind != JsonValueKind.Number
                    || !turnValue.TryGetInt64(out var parsedTurnId)
                    || parsedTurnId < 0)
                {
                    return false;
                }

                turnId = parsedTurnId;
            }

            long nextRevision;
            if (root.TryGetProperty("revision", out var revisionValue))
            {
                if (revisionValue.ValueKind != JsonValueKind.Number
                    || !revisionValue.TryGetInt64(out var parsedRevision)
                    || parsedRevision <= 0
                    || parsedRevision <= LastPartialRevision)
                {
                    return false;
                }

                nextRevision = parsedRevision;
            }
            else
            {
                nextRevision = LastPartialRevision + 1;
            }

            LastPartialRevision = nextRevision;
            revision = nextRevision;
            partial = new RealtimePartialTranscript(turnId, transcript);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Attempts to parse and record a finalized transcript turn from a provider JSON event.</summary>
    public bool TryAppend(string json)
        => TryAppend(json, out _);

    /// <summary>Attempts to parse and record a finalized transcript turn from a provider JSON event.</summary>
    public bool TryAppend(string json, out RealtimeTranscriptTurn? appended)
    {
        appended = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeValue)
                || typeValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("transcript", out var transcriptValue)
                || transcriptValue.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var type = typeValue.GetString();
            var transcript = transcriptValue.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return false;
            }

            if (string.Equals(type, "speechComplete", StringComparison.Ordinal))
            {
                if (!root.TryGetProperty("turnId", out var turnValue)
                    || turnValue.ValueKind != JsonValueKind.Number
                    || !turnValue.TryGetInt64(out var turnId)
                    || turnId < 0
                    || !_completedTurnIds.Add(turnId))
                {
                    return false;
                }

                appended = new RealtimeTranscriptTurn(turnId, transcript);
                _transcripts.Add(appended);
                return true;
            }

            if (!string.Equals(type, "transcript", StringComparison.Ordinal)
                || !root.TryGetProperty("final", out var finalValue)
                || finalValue.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            long? finalTurnId = null;
            if (root.TryGetProperty("turnId", out var finalTurnValue))
            {
                if (finalTurnValue.ValueKind != JsonValueKind.Number
                    || !finalTurnValue.TryGetInt64(out var parsedTurnId)
                    || parsedTurnId < 0
                    || !_completedTurnIds.Add(parsedTurnId))
                {
                    return false;
                }

                finalTurnId = parsedTurnId;
            }

            appended = new RealtimeTranscriptTurn(finalTurnId, transcript);
            _transcripts.Add(appended);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
