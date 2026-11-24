using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Core.Configuration;

namespace OpenConnect.Core;

/// <summary>Composite authority key scoping a duplex voice session to trusted tenant, principal, and session.</summary>
public readonly record struct VoiceSessionKey(TenantId Tenant, PrincipalId Principal, SessionId Session);

/// <summary>Point-in-time snapshot of the concurrent states and authoritative identifiers of a duplex voice session.</summary>
public sealed record DuplexVoiceSessionSnapshot(
    VoiceSessionKey Key,
    string VoiceConnectionId,
    VoiceConnectionState ConnectionState,
    VoiceInputState InputState,
    VoiceReasoningState ReasoningState,
    VoiceOutputState OutputState,
    long SpeechEpoch,
    string? ActiveCaptureId,
    string? ActiveResponseId,
    long LastPartialRevision,
    long TransmittedSamples,
    long LastConfirmedPlayedSamples,
    bool HasPlaybackReceipt);

/// <summary>Result of starting a new user utterance capture.</summary>
public sealed record DuplexCaptureResult(
    bool Accepted,
    string? CaptureId,
    long SpeechEpoch,
    string? RejectionReason);

/// <summary>Result of recording a non-authoritative partial transcript hypothesis.</summary>
public sealed record DuplexPartialResult(
    bool Accepted,
    long SpeechEpoch,
    long Revision,
    string? PartialText,
    string? RejectionReason);

/// <summary>Result of attempting to admit a final transcript for durable turn execution.</summary>
public sealed record DuplexFinalAdmissionResult(
    bool Admitted,
    bool IsDuplicate,
    string? TurnId,
    string? IdempotencyKey,
    long SpeechEpoch,
    string? Transcript,
    string? RejectionReason);

/// <summary>Result of starting an assistant response under the authoritative speech epoch.</summary>
public sealed record DuplexResponseStartResult(
    bool Accepted,
    string? ResponseId,
    long SpeechEpoch,
    string? RejectionReason);

/// <summary>Result of registering a synthesized audio segment for an active response.</summary>
public sealed record DuplexSegmentRegistrationResult(
    bool Accepted,
    long TransmittedSamples,
    long SpeechEpoch,
    string? RejectionReason);

/// <summary>Result of processing a client playback progress receipt.</summary>
public sealed record DuplexPlaybackReceiptResult(
    bool Accepted,
    long ConfirmedPlayedSamples,
    long TransmittedSamples,
    VoiceOutputState OutputState,
    string? RejectionReason);

/// <summary>Result of processing a client or gateway interruption request.</summary>
public sealed record DuplexInterruptResult(
    bool Accepted,
    bool WasIdempotentReplay,
    long PreviousSpeechEpoch,
    long NewSpeechEpoch,
    string? CancelledResponseId,
    VoiceDeliveryReconciliation? Reconciliation,
    string? RejectionReason);

/// <summary>Result of fencing a disconnected or replaced voice transport connection.</summary>
public sealed record DuplexConnectionFenceResult(
    bool Fenced,
    long SpeechEpoch,
    VoiceConnectionState ConnectionState,
    VoiceDeliveryReconciliation? Reconciliation,
    string? RejectionReason);

/// <summary>Gateway-authoritative coordinator for full-duplex <c>openconnect.voice.v2</c> sessions.</summary>
public sealed class DuplexVoiceSessionCoordinator
{
    private sealed class SessionState(VoiceSessionKey key, string voiceConnectionId)
    {
        public object Sync { get; } = new();
        public VoiceSessionKey Key { get; } = key;
        public string VoiceConnectionId { get; set; } = voiceConnectionId;
        public VoiceConnectionState ConnectionState { get; set; } = VoiceConnectionState.Ready;
        public VoiceInputState InputState { get; set; } = VoiceInputState.Listening;
        public VoiceReasoningState ReasoningState { get; set; } = VoiceReasoningState.Idle;
        public VoiceOutputState OutputState { get; set; } = VoiceOutputState.Idle;
        public long SpeechEpoch { get; set; } = 1;
        public string? ActiveCaptureId { get; set; }
        public long LastPartialRevision { get; set; }
        public string? ActiveResponseId { get; set; }
        public long TransmittedSamples { get; set; }
        public long LastConfirmedPlayedSamples { get; set; }
        public bool HasPlaybackReceipt { get; set; }
        public List<VoiceSegmentMetadata> Segments { get; } = [];
        public Dictionary<string, DuplexFinalAdmissionResult> AdmittedFinals { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DuplexInterruptResult> ProcessedInterrupts { get; } = new(StringComparer.Ordinal);
    }

    private readonly OpenConnectVoiceOptions _options;
    private readonly ConcurrentDictionary<VoiceSessionKey, SessionState> _sessions = new();

    public DuplexVoiceSessionCoordinator(IOptions<OpenConnectVoiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>Opens or rebinds a duplex voice session under a composite <c>(Tenant, Principal, Session)</c> key.</summary>
    public DuplexVoiceSessionSnapshot OpenSession(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string voiceConnectionId)
    {
        ValidateIdentity(tenant, principal, sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceConnectionId);

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        var state = _sessions.GetOrAdd(key, k => new SessionState(k, voiceConnectionId));
        lock (state.Sync)
        {
            if (!string.Equals(state.VoiceConnectionId, voiceConnectionId, StringComparison.Ordinal))
            {
                state.VoiceConnectionId = voiceConnectionId;
                state.ActiveCaptureId = null;
                state.LastPartialRevision = 0;
            }

            state.ConnectionState = VoiceConnectionState.Ready;
            if (state.InputState != VoiceInputState.Muted)
            {
                state.InputState = VoiceInputState.Listening;
            }

            return CreateSnapshot(state);
        }
    }

    /// <summary>Gets a snapshot of an existing session if present for the exact composite key.</summary>
    public DuplexVoiceSessionSnapshot? GetSnapshot(TenantId tenant, PrincipalId principal, SessionId sessionId)
    {
        if (tenant.Value == default || principal.Value == default || sessionId.Value == default)
        {
            return null;
        }

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return null;
        }

        lock (state.Sync)
        {
            return CreateSnapshot(state);
        }
    }

    /// <summary>Starts a new user utterance capture generation bound to the current authoritative speech epoch.</summary>
    public DuplexCaptureResult BeginCapture(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string captureId,
        string? voiceConnectionId = null)
    {
        ValidateIdentity(tenant, principal, sessionId);
        if (string.IsNullOrWhiteSpace(captureId))
        {
            return new DuplexCaptureResult(false, null, 0, ErrorCodes.InvalidRequest);
        }

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexCaptureResult(false, null, 0, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (state.ConnectionState != VoiceConnectionState.Ready)
            {
                return new DuplexCaptureResult(false, null, state.SpeechEpoch, "connection_not_ready");
            }

            if (voiceConnectionId is not null && !string.Equals(state.VoiceConnectionId, voiceConnectionId, StringComparison.Ordinal))
            {
                return new DuplexCaptureResult(false, null, state.SpeechEpoch, "stale_voice_connection");
            }

            if (state.InputState == VoiceInputState.Muted)
            {
                return new DuplexCaptureResult(false, null, state.SpeechEpoch, "microphone_muted");
            }

            state.ActiveCaptureId = captureId;
            state.LastPartialRevision = 0;
            state.InputState = VoiceInputState.SpeechDetected;
            return new DuplexCaptureResult(true, captureId, state.SpeechEpoch, null);
        }
    }

    /// <summary>Records a non-authoritative partial transcript hypothesis with monotonic revision checking. Never commits a turn.</summary>
    public DuplexPartialResult RecordPartial(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string captureId,
        long speechEpoch,
        long revision,
        string partialText)
    {
        ValidateIdentity(tenant, principal, sessionId);
        if (string.IsNullOrWhiteSpace(captureId) || revision <= 0 || string.IsNullOrWhiteSpace(partialText))
        {
            return new DuplexPartialResult(false, 0, 0, null, ErrorCodes.InvalidRequest);
        }

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexPartialResult(false, 0, 0, null, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (state.ConnectionState != VoiceConnectionState.Ready)
            {
                return new DuplexPartialResult(false, state.SpeechEpoch, state.LastPartialRevision, null, "connection_not_ready");
            }

            if (speechEpoch != state.SpeechEpoch)
            {
                return new DuplexPartialResult(false, state.SpeechEpoch, state.LastPartialRevision, null, "stale_speech_epoch");
            }

            if (!string.Equals(state.ActiveCaptureId, captureId, StringComparison.Ordinal))
            {
                return new DuplexPartialResult(false, state.SpeechEpoch, state.LastPartialRevision, null, "stale_capture_id");
            }

            if (revision <= state.LastPartialRevision)
            {
                return new DuplexPartialResult(false, state.SpeechEpoch, state.LastPartialRevision, null, "stale_partial_revision");
            }

            state.LastPartialRevision = revision;
            state.InputState = VoiceInputState.SpeechDetected;
            return new DuplexPartialResult(true, state.SpeechEpoch, revision, partialText.Trim(), null);
        }
    }

    /// <summary>Attempts to admit a finalized transcript with capture+epoch idempotency.</summary>
    public DuplexFinalAdmissionResult TryAdmitFinal(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string captureId,
        long speechEpoch,
        string finalTranscript)
    {
        if (tenant.Value == default || principal.Value == default)
        {
            return new DuplexFinalAdmissionResult(false, false, null, null, 0, null, ErrorCodes.Unauthorized);
        }

        if (!_options.EnableLiveTranscription && !_options.EnableRecordedTranscription)
        {
            return new DuplexFinalAdmissionResult(false, false, null, null, 0, null, ErrorCodes.CapabilityUnavailable);
        }

        if (sessionId.Value == default || string.IsNullOrWhiteSpace(captureId) || string.IsNullOrWhiteSpace(finalTranscript))
        {
            return new DuplexFinalAdmissionResult(false, false, null, null, 0, null, ErrorCodes.InvalidRequest);
        }

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexFinalAdmissionResult(false, false, null, null, 0, null, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (state.ConnectionState != VoiceConnectionState.Ready)
            {
                return new DuplexFinalAdmissionResult(false, false, null, null, state.SpeechEpoch, null, "connection_not_ready");
            }

            var idempotencyKey = $"voice:{captureId}:{speechEpoch}";
            if (state.AdmittedFinals.TryGetValue(idempotencyKey, out var existing))
            {
                return existing with { Admitted = false, IsDuplicate = true, RejectionReason = "duplicate_final_transcript" };
            }

            if (speechEpoch != state.SpeechEpoch)
            {
                return new DuplexFinalAdmissionResult(false, false, null, null, state.SpeechEpoch, null, "stale_speech_epoch");
            }

            if (!string.Equals(state.ActiveCaptureId, captureId, StringComparison.Ordinal))
            {
                return new DuplexFinalAdmissionResult(false, false, null, null, state.SpeechEpoch, null, "stale_capture_id");
            }

            var trimmed = finalTranscript.Trim();
            var result = new DuplexFinalAdmissionResult(
                Admitted: true,
                IsDuplicate: false,
                TurnId: Ulid.NewUlid().ToString(),
                IdempotencyKey: idempotencyKey,
                SpeechEpoch: state.SpeechEpoch,
                Transcript: trimmed,
                RejectionReason: null);

            state.AdmittedFinals[idempotencyKey] = result;
            state.InputState = state.InputState == VoiceInputState.Muted
                ? VoiceInputState.Muted
                : VoiceInputState.Listening;
            state.ActiveCaptureId = null;
            state.LastPartialRevision = 0;
            return result;
        }
    }

    /// <summary>Allocates and activates an assistant response under the current speech epoch.</summary>
    public DuplexResponseStartResult StartResponse(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string responseId,
        long speechEpoch)
    {
        ValidateIdentity(tenant, principal, sessionId);
        if (string.IsNullOrWhiteSpace(responseId))
        {
            return new DuplexResponseStartResult(false, null, 0, ErrorCodes.InvalidRequest);
        }

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexResponseStartResult(false, null, 0, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (state.ConnectionState != VoiceConnectionState.Ready)
            {
                return new DuplexResponseStartResult(false, null, state.SpeechEpoch, "connection_not_ready");
            }

            if (speechEpoch != state.SpeechEpoch)
            {
                return new DuplexResponseStartResult(false, null, state.SpeechEpoch, "stale_speech_epoch");
            }

            state.ActiveResponseId = responseId;
            state.ReasoningState = VoiceReasoningState.Generating;
            state.OutputState = VoiceOutputState.Buffering;
            state.TransmittedSamples = 0;
            state.LastConfirmedPlayedSamples = 0;
            state.HasPlaybackReceipt = false;
            state.Segments.Clear();

            return new DuplexResponseStartResult(true, responseId, state.SpeechEpoch, null);
        }
    }

    /// <summary>Registers an ordered synthesized audio segment for the active response.</summary>
    public DuplexSegmentRegistrationResult RegisterAudioSegment(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string responseId,
        long speechEpoch,
        VoiceSegmentMetadata segment)
    {
        ValidateIdentity(tenant, principal, sessionId);
        ArgumentNullException.ThrowIfNull(segment);

        if (string.IsNullOrWhiteSpace(responseId)
            || string.IsNullOrWhiteSpace(segment.SegmentId)
            || segment.SegmentIndex < 0
            || segment.StartSampleOffset < 0
            || segment.SampleCount < 0)
        {
            return new DuplexSegmentRegistrationResult(false, 0, 0, ErrorCodes.InvalidRequest);
        }

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexSegmentRegistrationResult(false, 0, 0, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (state.ConnectionState != VoiceConnectionState.Ready)
            {
                return new DuplexSegmentRegistrationResult(false, state.TransmittedSamples, state.SpeechEpoch, "connection_not_ready");
            }

            if (speechEpoch != state.SpeechEpoch)
            {
                return new DuplexSegmentRegistrationResult(false, state.TransmittedSamples, state.SpeechEpoch, "stale_speech_epoch");
            }

            if (!string.Equals(state.ActiveResponseId, responseId, StringComparison.Ordinal))
            {
                return new DuplexSegmentRegistrationResult(false, state.TransmittedSamples, state.SpeechEpoch, "response_mismatch");
            }

            if (segment.StartSampleOffset < state.TransmittedSamples)
            {
                return new DuplexSegmentRegistrationResult(false, state.TransmittedSamples, state.SpeechEpoch, "overlapping_segment_offset");
            }

            state.Segments.Add(segment);
            state.TransmittedSamples = Math.Max(state.TransmittedSamples, segment.EndSampleOffset);
            if (state.TransmittedSamples > 0 && state.OutputState is VoiceOutputState.Idle or VoiceOutputState.Buffering)
            {
                state.OutputState = VoiceOutputState.Playing;
            }

            return new DuplexSegmentRegistrationResult(true, state.TransmittedSamples, state.SpeechEpoch, null);
        }
    }

    /// <summary>Marks assistant response generation complete while playback may still be draining.</summary>
    public DuplexVoiceSessionSnapshot? CompleteResponseGeneration(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string responseId,
        long speechEpoch)
    {
        ValidateIdentity(tenant, principal, sessionId);
        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return null;
        }

        lock (state.Sync)
        {
            if (speechEpoch != state.SpeechEpoch
                || !string.Equals(state.ActiveResponseId, responseId, StringComparison.Ordinal))
            {
                return CreateSnapshot(state);
            }

            state.ReasoningState = VoiceReasoningState.Completed;
            if (state.TransmittedSamples == 0)
            {
                state.OutputState = VoiceOutputState.Idle;
            }
            else if (state.OutputState is VoiceOutputState.Buffering or VoiceOutputState.Playing)
            {
                state.OutputState = VoiceOutputState.Draining;
            }

            return CreateSnapshot(state);
        }
    }

    /// <summary>Validates and records an advisory client playback receipt bounded by transmitted samples.</summary>
    public DuplexPlaybackReceiptResult RecordPlaybackReceipt(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        VoicePlaybackReceipt receipt)
    {
        ValidateIdentity(tenant, principal, sessionId);
        ArgumentNullException.ThrowIfNull(receipt);

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexPlaybackReceiptResult(false, 0, 0, VoiceOutputState.Idle, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (receipt.SpeechEpoch != state.SpeechEpoch)
            {
                return new DuplexPlaybackReceiptResult(
                    false,
                    state.LastConfirmedPlayedSamples,
                    state.TransmittedSamples,
                    state.OutputState,
                    "stale_speech_epoch");
            }

            if (string.IsNullOrWhiteSpace(receipt.ResponseId)
                || !string.Equals(state.ActiveResponseId, receipt.ResponseId, StringComparison.Ordinal))
            {
                return new DuplexPlaybackReceiptResult(
                    false,
                    state.LastConfirmedPlayedSamples,
                    state.TransmittedSamples,
                    state.OutputState,
                    "response_mismatch");
            }

            if (receipt.PlayedSamples < 0 || receipt.PlayedSamples > state.TransmittedSamples)
            {
                return new DuplexPlaybackReceiptResult(
                    false,
                    state.LastConfirmedPlayedSamples,
                    state.TransmittedSamples,
                    state.OutputState,
                    "invalid_playback_offset");
            }

            if (state.HasPlaybackReceipt && receipt.PlayedSamples < state.LastConfirmedPlayedSamples)
            {
                return new DuplexPlaybackReceiptResult(
                    false,
                    state.LastConfirmedPlayedSamples,
                    state.TransmittedSamples,
                    state.OutputState,
                    "non_monotonic_playback_offset");
            }

            state.LastConfirmedPlayedSamples = receipt.PlayedSamples;
            state.HasPlaybackReceipt = true;

            if (receipt.Completed && receipt.PlayedSamples == state.TransmittedSamples && state.ReasoningState == VoiceReasoningState.Completed)
            {
                state.OutputState = VoiceOutputState.Idle;
            }
            else if (state.OutputState == VoiceOutputState.Buffering && receipt.PlayedSamples > 0)
            {
                state.OutputState = VoiceOutputState.Playing;
            }

            return new DuplexPlaybackReceiptResult(
                true,
                state.LastConfirmedPlayedSamples,
                state.TransmittedSamples,
                state.OutputState,
                null);
        }
    }

    /// <summary>Atomically handles an interruption request with <c>RequestId</c> idempotency, epoch advancement, and delivery reconciliation.</summary>
    public DuplexInterruptResult HandleInterrupt(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        VoiceInterruptRequest request)
    {
        ValidateIdentity(tenant, principal, sessionId);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.PlayedSamples < 0)
        {
            return new DuplexInterruptResult(false, false, 0, 0, null, null, ErrorCodes.InvalidRequest);
        }

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexInterruptResult(false, false, 0, 0, null, null, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (state.ProcessedInterrupts.TryGetValue(request.RequestId, out var existing))
            {
                return existing with { WasIdempotentReplay = true };
            }

            if (request.SpeechEpoch != state.SpeechEpoch)
            {
                return new DuplexInterruptResult(
                    false,
                    false,
                    state.SpeechEpoch,
                    state.SpeechEpoch,
                    state.ActiveResponseId,
                    null,
                    "stale_speech_epoch");
            }

            if (request.ResponseId is not null
                && state.ActiveResponseId is not null
                && !string.Equals(request.ResponseId, state.ActiveResponseId, StringComparison.Ordinal))
            {
                return new DuplexInterruptResult(
                    false,
                    false,
                    state.SpeechEpoch,
                    state.SpeechEpoch,
                    state.ActiveResponseId,
                    null,
                    "response_mismatch");
            }

            var previousEpoch = state.SpeechEpoch;
            var cancelledResponseId = state.ActiveResponseId;

            if (cancelledResponseId is not null
                && request.PlayedSamples <= state.TransmittedSamples
                && (!state.HasPlaybackReceipt || request.PlayedSamples >= state.LastConfirmedPlayedSamples))
            {
                if (request.PlayedSamples > 0 || state.HasPlaybackReceipt)
                {
                    state.LastConfirmedPlayedSamples = request.PlayedSamples;
                    state.HasPlaybackReceipt = true;
                }
            }

            VoiceDeliveryReconciliation? reconciliation = cancelledResponseId is not null
                ? ReconcileDelivery(
                    cancelledResponseId,
                    previousEpoch,
                    state.TransmittedSamples,
                    state.LastConfirmedPlayedSamples,
                    state.HasPlaybackReceipt,
                    state.Segments)
                : null;

            state.SpeechEpoch = previousEpoch + 1;
            state.ReasoningState = cancelledResponseId is not null && state.ReasoningState is VoiceReasoningState.Generating or VoiceReasoningState.AwaitingTool
                ? VoiceReasoningState.Cancelling
                : VoiceReasoningState.Idle;
            state.OutputState = VoiceOutputState.Interrupted;
            state.ActiveResponseId = null;
            state.TransmittedSamples = 0;
            state.LastConfirmedPlayedSamples = 0;
            state.HasPlaybackReceipt = false;
            state.Segments.Clear();

            var result = new DuplexInterruptResult(
                Accepted: true,
                WasIdempotentReplay: false,
                PreviousSpeechEpoch: previousEpoch,
                NewSpeechEpoch: state.SpeechEpoch,
                CancelledResponseId: cancelledResponseId,
                Reconciliation: reconciliation,
                RejectionReason: null);

            state.ProcessedInterrupts[request.RequestId] = result;
            return result;
        }
    }

    /// <summary>Fences a disconnected or superseded transport connection, advancing epoch if work was active and marking delivery uncertain.</summary>
    public DuplexConnectionFenceResult FenceConnection(
        TenantId tenant,
        PrincipalId principal,
        SessionId sessionId,
        string voiceConnectionId,
        bool markReconnecting = false)
    {
        ValidateIdentity(tenant, principal, sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceConnectionId);

        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return new DuplexConnectionFenceResult(false, 0, VoiceConnectionState.Closed, null, ErrorCodes.SessionNotFound);
        }

        lock (state.Sync)
        {
            if (!string.Equals(state.VoiceConnectionId, voiceConnectionId, StringComparison.Ordinal))
            {
                return new DuplexConnectionFenceResult(false, state.SpeechEpoch, state.ConnectionState, null, "stale_voice_connection");
            }

            VoiceDeliveryReconciliation? reconciliation = null;
            if (state.ActiveResponseId is not null)
            {
                // Disconnect during playback: mark delivery uncertain (never assume unreceipted audio was heard).
                reconciliation = ReconcileDelivery(
                    state.ActiveResponseId,
                    state.SpeechEpoch,
                    state.TransmittedSamples,
                    state.LastConfirmedPlayedSamples,
                    receiptReceived: state.HasPlaybackReceipt,
                    state.Segments);
                state.SpeechEpoch++;
                state.OutputState = VoiceOutputState.Interrupted;
                state.ReasoningState = VoiceReasoningState.Idle;
                state.ActiveResponseId = null;
                state.TransmittedSamples = 0;
                state.LastConfirmedPlayedSamples = 0;
                state.HasPlaybackReceipt = false;
                state.Segments.Clear();
            }

            state.ActiveCaptureId = null;
            state.LastPartialRevision = 0;
            state.ConnectionState = markReconnecting ? VoiceConnectionState.Reconnecting : VoiceConnectionState.Closed;

            return new DuplexConnectionFenceResult(true, state.SpeechEpoch, state.ConnectionState, reconciliation, null);
        }
    }

    /// <summary>Updates microphone mute state without affecting reasoning or output state.</summary>
    public DuplexVoiceSessionSnapshot? SetMuted(TenantId tenant, PrincipalId principal, SessionId sessionId, bool muted)
    {
        ValidateIdentity(tenant, principal, sessionId);
        var key = new VoiceSessionKey(tenant, principal, sessionId);
        if (!_sessions.TryGetValue(key, out var state))
        {
            return null;
        }

        lock (state.Sync)
        {
            state.InputState = muted ? VoiceInputState.Muted : VoiceInputState.Listening;
            return CreateSnapshot(state);
        }
    }

    private static VoiceDeliveryReconciliation ReconcileDelivery(
        string responseId,
        long speechEpoch,
        long transmittedSamples,
        long reportedPlayedSamples,
        bool receiptReceived,
        IReadOnlyList<VoiceSegmentMetadata> segments)
    {
        if (segments.Count == 0 || transmittedSamples == 0)
        {
            return new VoiceDeliveryReconciliation(
                responseId,
                speechEpoch,
                transmittedSamples,
                reportedPlayedSamples,
                receiptReceived,
                Array.Empty<VoiceSegmentMetadata>(),
                null,
                Array.Empty<VoiceSegmentMetadata>(),
                string.Empty,
                null);
        }

        var confirmed = new List<VoiceSegmentMetadata>();
        VoiceSegmentMetadata? uncertain = null;
        var unheard = new List<VoiceSegmentMetadata>();

        if (!receiptReceived)
        {
            // Missing receipt means delivery is unknown, not complete.
            uncertain = segments[0];
            for (var i = 1; i < segments.Count; i++)
            {
                unheard.Add(segments[i]);
            }
        }
        else
        {
            foreach (var segment in segments)
            {
                if (reportedPlayedSamples >= segment.EndSampleOffset)
                {
                    confirmed.Add(segment);
                }
                else if (reportedPlayedSamples > segment.StartSampleOffset && reportedPlayedSamples < segment.EndSampleOffset)
                {
                    uncertain = segment;
                }
                else
                {
                    unheard.Add(segment);
                }
            }
        }

        var confirmedText = string.Join(" ", confirmed.Select(s => s.SpokenText).Where(t => !string.IsNullOrWhiteSpace(t)));
        return new VoiceDeliveryReconciliation(
            responseId,
            speechEpoch,
            transmittedSamples,
            reportedPlayedSamples,
            receiptReceived,
            confirmed,
            uncertain,
            unheard,
            confirmedText,
            uncertain?.SpokenText);
    }

    private static DuplexVoiceSessionSnapshot CreateSnapshot(SessionState state) => new(
        state.Key,
        state.VoiceConnectionId,
        state.ConnectionState,
        state.InputState,
        state.ReasoningState,
        state.OutputState,
        state.SpeechEpoch,
        state.ActiveCaptureId,
        state.ActiveResponseId,
        state.LastPartialRevision,
        state.TransmittedSamples,
        state.LastConfirmedPlayedSamples,
        state.HasPlaybackReceipt);

    private static void ValidateIdentity(TenantId tenant, PrincipalId principal, SessionId sessionId)
    {
        if (tenant.Value == default || principal.Value == default)
        {
            throw new UnauthorizedAccessException("Authenticated tenant and principal are required for duplex voice coordination.");
        }

        if (sessionId.Value == default)
        {
            throw new ArgumentException("A valid session ID is required.", nameof(sessionId));
        }
    }
}
