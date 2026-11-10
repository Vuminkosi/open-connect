using System.Security.Cryptography;
using OpenConnect.Abstractions;
using OpenConnect.Client;
using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// UI-agnostic session state machine. Pure C# with no Blazor dependencies so it
/// is fully unit-testable without bUnit. The provider owns one instance and
/// cascades it; the composer reads and mutates it through these methods.
/// </summary>
public sealed class ComposerSession
{
    private static readonly HashSet<string> AllowedRecordingMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/wav",
        "audio/x-wav",
        "audio/wave",
        "audio/webm",
        "audio/ogg",
        "audio/pcm",
    };

    private const int MaxRecordingBytesHardCap = 10 * 1024 * 1024;
    private static readonly TimeSpan MaxRecordingDurationHardCap = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly List<ComposerMessage> _messages = new();
    private readonly List<ComponentProposal> _proposals = new();
    private readonly List<string> _errors = new();
    private string? _streamingRole;
    private string _streamingText = string.Empty;
    private string _livePartialTranscript = string.Empty;

    public string? SessionId { get; private set; }

    public ComposerConnectionState Connection { get; private set; } = ComposerConnectionState.Idle;

    public ComposerMicrophoneState MicrophoneState { get; private set; } = ComposerMicrophoneState.Off;

    public ComposerSpeechState SpeechState { get; private set; } = ComposerSpeechState.Off;

    public long SpeechEpoch { get; private set; }

    public long PlaybackEpoch { get; private set; }

    public RecordedVoiceDraft? StagedRecording { get; private set; }

    public IReadOnlyList<ComposerMessage> Messages
    {
        get { lock (_gate) { return _messages.ToList(); } }
    }

    public IReadOnlyList<ComponentProposal> Proposals
    {
        get { lock (_gate) { return _proposals.ToList(); } }
    }

    public string? LastError
    {
        get { lock (_gate) { return _errors.Count == 0 ? null : _errors[^1]; } }
    }

    public long LastSequence { get; private set; }

    public string StreamingText
    {
        get { lock (_gate) { return _streamingText; } }
    }

    public string LivePartialTranscript
    {
        get { lock (_gate) { return _livePartialTranscript; } }
    }

    public event Action? Changed;

    public void AttachSession(string sessionId)
    {
        lock (_gate)
        {
            SessionId = sessionId;
            Connection = ComposerConnectionState.Idle;
            LastSequence = 0;
            _messages.Clear();
            _proposals.Clear();
            _errors.Clear();
            _streamingText = string.Empty;
            _streamingRole = null;
            _livePartialTranscript = string.Empty;
            StagedRecording = null;
            MicrophoneState = ComposerMicrophoneState.Off;
            SpeechState = ComposerSpeechState.Off;
            SpeechEpoch++;
            PlaybackEpoch++;
        }

        Notify();
    }

    public void BeginSend(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_gate)
        {
            _messages.Add(new ComposerMessage("user", text.Trim(), LastSequence, ComposerMessageState.Received));
            _streamingRole = "assistant";
            _streamingText = string.Empty;
            Connection = ComposerConnectionState.Streaming;
        }

        Notify();
    }

    /// <summary>
    /// Starts live microphone capture after an explicit user gesture.
    /// Denying permission fails closed without opening capture.
    /// </summary>
    public long StartLiveListening(bool permissionGranted)
    {
        lock (_gate)
        {
            if (!permissionGranted)
            {
                MicrophoneState = ComposerMicrophoneState.Failed;
                _errors.Add("Microphone permission denied.");
                SpeechEpoch++;
                Notify();
                return SpeechEpoch;
            }

            SpeechEpoch++;
            _livePartialTranscript = string.Empty;
            MicrophoneState = ComposerMicrophoneState.ListeningLive;
            if (SpeechState == ComposerSpeechState.Playing)
            {
                PlaybackEpoch++;
                SpeechState = ComposerSpeechState.On;
            }
        }

        Notify();
        return SpeechEpoch;
    }

    /// <summary>
    /// Updates the disposable live STT partial hypothesis for display only.
    /// Never commits a user turn or triggers tools. Rejects stale speech epochs.
    /// </summary>
    public bool UpdateLiveHypothesis(string partialText, long speechEpoch)
    {
        ArgumentNullException.ThrowIfNull(partialText);
        lock (_gate)
        {
            if (speechEpoch == 0 || speechEpoch != SpeechEpoch || MicrophoneState != ComposerMicrophoneState.ListeningLive)
            {
                return false;
            }

            _livePartialTranscript = partialText.Trim();
        }

        Notify();
        return true;
    }

    /// <summary>
    /// Commits a gateway-authorized final live STT transcript as a single user turn.
    /// Rejects stale speech epochs or empty transcripts.
    /// </summary>
    public bool CommitLiveTranscript(string finalTranscript, long speechEpoch)
    {
        if (string.IsNullOrWhiteSpace(finalTranscript))
        {
            return false;
        }

        lock (_gate)
        {
            if (speechEpoch == 0 || speechEpoch != SpeechEpoch)
            {
                return false;
            }

            _livePartialTranscript = string.Empty;
            MicrophoneState = ComposerMicrophoneState.Off;
            _messages.Add(new ComposerMessage("user", finalTranscript.Trim(), LastSequence, ComposerMessageState.Received));
            _streamingRole = "assistant";
            _streamingText = string.Empty;
            Connection = ComposerConnectionState.Streaming;
        }

        Notify();
        return true;
    }

    /// <summary>
    /// Starts a bounded voice recording session (distinct from live STT).
    /// </summary>
    public void StartVoiceRecording(bool permissionGranted)
    {
        lock (_gate)
        {
            if (!permissionGranted)
            {
                MicrophoneState = ComposerMicrophoneState.Failed;
                _errors.Add("Microphone permission denied.");
            }
            else
            {
                StagedRecording = null;
                MicrophoneState = ComposerMicrophoneState.Recording;
            }
        }

        Notify();
    }

    /// <summary>
    /// Stages a recorded audio file for user preview before upload.
    /// Validates MIME type, byte bounds, duration, and SHA-256 checksum. Does not auto-submit.
    /// </summary>
    public RecordedVoiceDraft StageRecordedVoiceForPreview(ReadOnlySpan<byte> audioBytes, string mimeType, TimeSpan duration)
    {
        if (audioBytes.IsEmpty || audioBytes.Length > MaxRecordingBytesHardCap)
        {
            throw new ArgumentOutOfRangeException(nameof(audioBytes), "Recorded audio size is out of bounds.");
        }

        if (string.IsNullOrWhiteSpace(mimeType) || !AllowedRecordingMimeTypes.Contains(mimeType.Trim()))
        {
            throw new ArgumentException($"Unsupported audio MIME type '{mimeType}'.", nameof(mimeType));
        }

        if (duration <= TimeSpan.Zero || duration > MaxRecordingDurationHardCap)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Recording duration is out of bounds.");
        }

        var checksum = Convert.ToHexString(SHA256.HashData(audioBytes)).ToLowerInvariant();
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
            MicrophoneState = ComposerMicrophoneState.Off;
        }

        Notify();
        return draft;
    }

    /// <summary>Discards the staged recorded voice preview without submitting.</summary>
    public void DiscardRecordedVoice()
    {
        lock (_gate)
        {
            StagedRecording = null;
            if (MicrophoneState == ComposerMicrophoneState.Recording)
            {
                MicrophoneState = ComposerMicrophoneState.Off;
            }
        }

        Notify();
    }

    /// <summary>
    /// Accepts the staged recorded voice file for upload only when explicit user consent is granted.
    /// </summary>
    public RecordedVoiceDraft AcceptRecordedVoiceForSubmission(bool userConsentGranted)
    {
        if (!userConsentGranted)
        {
            throw new InvalidOperationException("User consent is required before submitting a recorded voice file.");
        }

        RecordedVoiceDraft draft;
        lock (_gate)
        {
            draft = StagedRecording ?? throw new InvalidOperationException("No staged voice recording is available to submit.");
            StagedRecording = null;
            MicrophoneState = ComposerMicrophoneState.Transcribing;
        }

        Notify();
        return draft;
    }

    /// <summary>Begins a new TTS playback epoch.</summary>
    public long BeginSpeechPlayback()
    {
        lock (_gate)
        {
            PlaybackEpoch++;
            SpeechState = ComposerSpeechState.Playing;
        }

        Notify();
        return PlaybackEpoch;
    }

    /// <summary>
    /// Validates whether a TTS audio chunk belongs to the active playback epoch.
    /// Returns false if barge-in incremented the epoch or playback stopped.
    /// </summary>
    public bool TryAcceptTtsAudioChunk(long chunkEpoch)
    {
        lock (_gate)
        {
            return SpeechState == ComposerSpeechState.Playing && chunkEpoch != 0 && chunkEpoch == PlaybackEpoch;
        }
    }

    /// <summary>
    /// Locally interrupts active TTS playback via speech epoch fencing.
    /// </summary>
    public long TriggerBargeIn()
    {
        lock (_gate)
        {
            SpeechEpoch++;
            PlaybackEpoch++;
            if (SpeechState == ComposerSpeechState.Playing || SpeechState == ComposerSpeechState.Buffering)
            {
                SpeechState = ComposerSpeechState.On;
            }
        }

        Notify();
        return PlaybackEpoch;
    }

    /// <summary>
    /// Invalidates all active speech/playback epochs and clears transient voice state on disposal/logout.
    /// </summary>
    public void DisposeVoiceState()
    {
        lock (_gate)
        {
            SpeechEpoch++;
            PlaybackEpoch++;
            _livePartialTranscript = string.Empty;
            StagedRecording = null;
            MicrophoneState = ComposerMicrophoneState.Off;
            SpeechState = ComposerSpeechState.Off;
        }

        Notify();
    }

    /// <summary>
    /// Applies one gateway event. Returns true when the UI should re-render.
    /// Unknown event types are ignored (never rendered as markup).
    /// Reconnect replay is idempotent: a record with an explicit sequence
    /// (Sequence != 0) at or below <see cref="LastSequence"/> is stale and is
    /// ignored without mutating messages, streaming text, errors, or the
    /// cursor. Sequence 0 means "no cursor assigned" (live/unsequenced event)
    /// and is always processed without moving the cursor.
    /// </summary>
    public bool ApplyEvent(EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var notify = true;
        lock (_gate)
        {
            if (record.Sequence != 0 && record.Sequence <= LastSequence)
            {
                return false;
            }

            if (record.Sequence > LastSequence)
            {
                LastSequence = record.Sequence;
            }

            var type = record.Type ?? string.Empty;
            if (type.EndsWith("delta", StringComparison.OrdinalIgnoreCase)
                || type.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                _streamingText += record.Data ?? string.Empty;
                Connection = ComposerConnectionState.Streaming;
            }
            else if (type.EndsWith("completed", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("done", StringComparison.OrdinalIgnoreCase))
            {
                if (_streamingText.Length > 0 || _streamingRole is not null)
                {
                    _messages.Add(new ComposerMessage(_streamingRole ?? "assistant", _streamingText, record.Sequence));
                    _streamingText = string.Empty;
                    _streamingRole = null;
                }

                if (Connection == ComposerConnectionState.Streaming)
                {
                    Connection = ComposerConnectionState.Idle;
                }
            }
            else if (type.EndsWith("failed", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                _errors.Add(string.IsNullOrWhiteSpace(record.Data) ? "The assistant could not complete the request." : record.Data);
                _messages.Add(new ComposerMessage("assistant", _streamingText, record.Sequence, ComposerMessageState.Failed));
                _streamingText = string.Empty;
                _streamingRole = null;
                Connection = ComposerConnectionState.Error;
            }
            else if (type.EndsWith("proposal", StringComparison.OrdinalIgnoreCase))
            {
                var proposal = TryParseProposal(record.Data);
                if (proposal is not null)
                {
                    _proposals.Add(proposal);
                }
                else
                {
                    notify = false;
                }
            }
            else
            {
                notify = false;
            }
        }

        if (notify)
        {
            Notify();
        }

        return notify;
    }

    public void MarkConnecting()
    {
        lock (_gate)
        {
            Connection = Connection == ComposerConnectionState.Streaming
                ? ComposerConnectionState.Streaming
                : ComposerConnectionState.Connecting;
        }

        Notify();
    }

    public void MarkReconnecting(string? message = null)
    {
        lock (_gate)
        {
            Connection = ComposerConnectionState.Reconnecting;
            if (!string.IsNullOrWhiteSpace(message))
            {
                _errors.Add(message!);
            }
        }

        Notify();
    }

    public void MarkError(string message)
    {
        lock (_gate)
        {
            Connection = ComposerConnectionState.Error;
            _errors.Add(string.IsNullOrWhiteSpace(message) ? "Connection failed." : message);
        }

        Notify();
    }

    public void CancelStreaming()
    {
        lock (_gate)
        {
            if (_streamingText.Length > 0 || _streamingRole is not null)
            {
                _messages.Add(new ComposerMessage(_streamingRole ?? "assistant", _streamingText, LastSequence, ComposerMessageState.Cancelled));
            }

            _streamingText = string.Empty;
            _streamingRole = null;
            if (Connection == ComposerConnectionState.Streaming)
            {
                Connection = ComposerConnectionState.Idle;
            }
        }

        Notify();
    }

    /// <summary>
    /// Clears the currently surfaced error without touching messages or
    /// connection state. Used by the composer's dismiss/retry affordances.
    /// </summary>
    public void DismissError()
    {
        lock (_gate)
        {
            _errors.Clear();
            if (Connection == ComposerConnectionState.Error)
            {
                Connection = ComposerConnectionState.Idle;
            }
        }

        Notify();
    }

    private static ComponentProposal? TryParseProposal(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("name", out var nameProp)
                || nameProp.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name) || !OpenConnectComponentDescriptor.IsValidName(name))
            {
                return null;
            }

            string? parameters = null;
            if (doc.RootElement.TryGetProperty("parameters", out var parametersProp)
                && parametersProp.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                parameters = parametersProp.GetRawText();
            }

            return new ComponentProposal(name, parameters);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void Notify() => Changed?.Invoke();
}
