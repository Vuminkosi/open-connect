"""Concurrent acoustic observation tracking and adaptive endpoint evaluation.

This module implements §4.1 (concurrent input/reasoning/output observation state)
and §7.2 (adaptive endpointing heuristics) for the realtime-voice worker.

Authority boundary:
- The .NET gateway is the sole authoritative owner of session state, turn
  admission, and ``speech_epoch``.
- This worker NEVER advances or mutates ``speech_epoch`` on its own. It only
  records the gateway-issued ``speech_epoch`` and fences/rejects stale epochs.
- All returned ``EndpointObservation`` values are strictly non-authoritative
  (``authoritative=False``).
"""

from __future__ import annotations

import re
import threading
import time
from dataclasses import dataclass, field, replace
from typing import Literal

ConnectionState = Literal[
    "opening",
    "ready",
    "reconnecting",
    "closing",
    "closed",
    "faulted",
]
InputState = Literal[
    "listening",
    "speech_detected",
    "endpoint_pending",
    "finalizing",
    "muted",
]
ReasoningState = Literal[
    "idle",
    "generating",
    "awaiting_tool",
    "awaiting_confirmation",
    "cancelling",
    "completed",
    "failed",
]
OutputState = Literal[
    "idle",
    "buffering",
    "playing",
    "draining",
    "interrupted",
    "failed",
]
EndpointDecision = Literal[
    "continue_listening",
    "endpoint_ready",
    "backchannel_ignore",
]
EndpointingMode = Literal["adaptive", "conservative"]

_ASSISTANT_SPEAKING_STATES = frozenset({"buffering", "playing", "draining"})

_HESITATION_FILLERS = frozenset(
    {
        "um",
        "uh",
        "umm",
        "uhh",
        "er",
        "erm",
        "hmm",
        "hm",
        "like",
        "so",
        "well",
        "actually",
        "basically",
    }
)
_HESITATION_PHRASES = (
    "you know",
    "i mean",
    "kind of",
    "sort of",
    "let me think",
    "hold on",
    "wait a second",
)
_TRAILING_INCOMPLETE_TOKENS = frozenset(
    {
        "and",
        "or",
        "but",
        "because",
        "since",
        "while",
        "if",
        "that",
        "which",
        "who",
        "where",
        "when",
        "although",
        "though",
        "unless",
        "until",
        "the",
        "a",
        "an",
        "to",
        "of",
        "in",
        "for",
        "on",
        "with",
        "at",
        "by",
        "from",
        "about",
        "into",
        "through",
        "during",
        "before",
        "after",
        "between",
        "under",
        "over",
        "my",
        "your",
        "our",
        "their",
        "his",
        "her",
        "this",
        "these",
        "those",
    }
)
_BACKCHANNEL_PHRASES = frozenset(
    {
        "mhm",
        "mm-hmm",
        "mmhmm",
        "mm hmm",
        "uh-huh",
        "uh huh",
        "uhhuh",
        "yeah",
        "yep",
        "yes",
        "ok",
        "okay",
        "right",
        "sure",
        "got it",
        "i see",
        "aha",
        "ah",
        "hmm",
        "hm",
    }
)
_CANONICAL_BACKCHANNELS = frozenset(
    {
        "mhm",
        "mm-hmm",
        "mmhmm",
        "mm hmm",
        "uh-huh",
        "uh huh",
        "uhhuh",
    }
)

_TOKEN_RE = re.compile(r"[a-zA-Z0-9'-]+")
_TRAILING_CONTINUATION_RE = re.compile(r"(?:\.\.\.|…|,|-|—|:)\s*$")
_TERMINAL_PUNCTUATION_RE = re.compile(r"(?<!\.\.)[.?!]\s*$")


class ObservationSessionNotFound(Exception):
    """Raised when an observation session does not exist for (tenant_id, session_id)."""


class StaleObservationEpoch(Exception):
    """Raised when a request presents a speech_epoch older than the gateway-issued epoch."""

    def __init__(self, expected: int, presented: int) -> None:
        super().__init__(f"stale speech epoch: expected >= {expected}, got {presented}")
        self.expected = expected
        self.presented = presented


@dataclass(frozen=True)
class EndpointObservation:
    """Non-authoritative endpointing recommendation produced by the worker."""

    decision: EndpointDecision
    confidence: float
    recommended_silence_ms: int
    reason: str
    authoritative: Literal[False] = False

    def to_dict(self) -> dict[str, object]:
        clamped = round(max(0.0, min(1.0, float(self.confidence))), 4)
        return {
            "decision": self.decision,
            "confidence": clamped,
            "recommendedSilenceMs": int(self.recommended_silence_ms),
            "reason": self.reason,
            "authoritative": False,
        }


@dataclass
class SessionObservation:
    """Independent concurrent observation states bound to (tenant_id, session_id)."""

    tenant_id: str
    session_id: str
    speech_epoch: int
    capture_id: str | None = None
    connection_state: ConnectionState = "ready"
    input_state: InputState = "listening"
    reasoning_state: ReasoningState = "idle"
    output_state: OutputState = "idle"
    last_transcript_revision: int | None = None
    last_transcript_text: str = ""
    stable_text: str = ""
    unstable_text: str = ""
    updated_at: float = field(default_factory=time.time)

    def to_dict(self) -> dict[str, object]:
        return {
            "tenantId": self.tenant_id,
            "sessionId": self.session_id,
            "speechEpoch": self.speech_epoch,
            "captureId": self.capture_id,
            "connectionState": self.connection_state,
            "inputState": self.input_state,
            "reasoningState": self.reasoning_state,
            "outputState": self.output_state,
            "transcriptRevision": self.last_transcript_revision,
            "authoritative": False,
        }


def _normalize_phrase(text: str) -> str:
    tokens = _TOKEN_RE.findall(text.lower())
    return " ".join(tokens)


def _compose_transcript(
    transcript: str | None,
    stable_text: str | None,
    unstable_text: str | None,
) -> str:
    if transcript is not None and transcript.strip():
        return transcript.strip()
    parts = [p.strip() for p in (stable_text, unstable_text) if p and p.strip()]
    return " ".join(parts)


class EndpointEvaluator:
    """Adaptive endpointing heuristic evaluator (§7.2).

    Combines acoustic silence duration, transcript revision stability,
    terminal punctuation/completeness, hesitation/filler tokens, and
    short backchannel detection during assistant playback.
    """

    def __init__(
        self,
        *,
        clear_silence_ms: int = 300,
        neutral_silence_ms: int = 550,
        hesitation_silence_ms: int = 1000,
        unstable_silence_ms: int = 650,
        conservative_delta_ms: int = 200,
        max_backchannel_speech_ms: int = 900,
    ) -> None:
        self._clear_silence_ms = clear_silence_ms
        self._neutral_silence_ms = neutral_silence_ms
        self._hesitation_silence_ms = hesitation_silence_ms
        self._unstable_silence_ms = unstable_silence_ms
        self._conservative_delta_ms = conservative_delta_ms
        self._max_backchannel_speech_ms = max_backchannel_speech_ms

    def _threshold(self, base_ms: int, mode: EndpointingMode) -> int:
        if mode == "conservative":
            return base_ms + self._conservative_delta_ms
        return base_ms

    def evaluate(
        self,
        *,
        silence_ms: int,
        transcript: str | None = None,
        stable_text: str | None = None,
        unstable_text: str | None = None,
        transcript_revision: int | None = None,
        previous_revision: int | None = None,
        previous_transcript: str | None = None,
        speech_duration_ms: int | None = None,
        assistant_speaking: bool = False,
        mode: EndpointingMode = "adaptive",
    ) -> EndpointObservation:
        text = _compose_transcript(transcript, stable_text, unstable_text)
        normalized = _normalize_phrase(text)
        tokens = normalized.split() if normalized else []

        # 1. Empty / silence-only transcript never endpoints.
        if not tokens:
            return EndpointObservation(
                decision="continue_listening",
                confidence=0.9,
                recommended_silence_ms=self._threshold(700, mode),
                reason="empty_transcript",
            )

        # 2. Short backchannel during assistant speech.
        if (
            assistant_speaking
            and normalized in _BACKCHANNEL_PHRASES
            and (speech_duration_ms is None or speech_duration_ms <= self._max_backchannel_speech_ms)
        ):
            confidence = 0.9 if normalized in _CANONICAL_BACKCHANNELS else 0.84
            return EndpointObservation(
                decision="backchannel_ignore",
                confidence=confidence,
                recommended_silence_ms=0,
                reason="backchannel_during_assistant_speech",
            )

        has_unstable = bool(unstable_text and unstable_text.strip())
        has_trailing_continuation = bool(_TRAILING_CONTINUATION_RE.search(text))
        has_terminal_punctuation = bool(
            _TERMINAL_PUNCTUATION_RE.search(text) and not has_trailing_continuation
        )

        last_token = tokens[-1]
        ends_with_hesitation_phrase = any(
            normalized == phrase or normalized.endswith(f" {phrase}")
            for phrase in _HESITATION_PHRASES
        )
        has_hesitation_filler = (
            last_token in _HESITATION_FILLERS
            or ends_with_hesitation_phrase
            or (has_trailing_continuation and last_token in _HESITATION_FILLERS)
        )
        has_trailing_incomplete = (
            last_token in _TRAILING_INCOMPLETE_TOKENS or has_trailing_continuation
        )

        # 3. Hesitation / filler / incomplete clause waiting (§7.2: 700-1200ms).
        if has_hesitation_filler or has_trailing_incomplete:
            hesitation_threshold = self._threshold(self._hesitation_silence_ms, mode)
            reason = (
                "hesitation_filler_detected"
                if has_hesitation_filler
                else "trailing_incomplete_phrase"
            )
            if silence_ms < hesitation_threshold or has_unstable:
                confidence = 0.86 if has_hesitation_filler else 0.78
                return EndpointObservation(
                    decision="continue_listening",
                    confidence=confidence,
                    recommended_silence_ms=hesitation_threshold,
                    reason=reason,
                )
            return EndpointObservation(
                decision="endpoint_ready",
                confidence=0.7,
                recommended_silence_ms=hesitation_threshold,
                reason="hesitation_timeout_elapsed",
            )

        # 4. Transcript stability check (unstable hypothesis text or active revision delta).
        revision_changed = (
            previous_revision is not None
            and transcript_revision is not None
            and transcript_revision > previous_revision
            and previous_transcript is not None
            and _normalize_phrase(previous_transcript) != normalized
        )
        if has_unstable:
            unstable_threshold = self._threshold(self._unstable_silence_ms, mode)
            if silence_ms < unstable_threshold:
                return EndpointObservation(
                    decision="continue_listening",
                    confidence=0.76,
                    recommended_silence_ms=unstable_threshold,
                    reason="transcript_unstable",
                )

        if revision_changed:
            rev_threshold = self._threshold(
                self._clear_silence_ms if has_terminal_punctuation else self._neutral_silence_ms,
                mode,
            )
            if silence_ms < rev_threshold:
                return EndpointObservation(
                    decision="continue_listening",
                    confidence=0.74,
                    recommended_silence_ms=rev_threshold,
                    reason="transcript_revision_active",
                )

        # 5. Clear completed utterance with terminal punctuation (§7.2: 250-400ms).
        if has_terminal_punctuation:
            clear_threshold = self._threshold(self._clear_silence_ms, mode)
            if silence_ms >= clear_threshold:
                bonus = min(0.06, (silence_ms - clear_threshold) / 2000.0)
                return EndpointObservation(
                    decision="endpoint_ready",
                    confidence=0.92 + bonus,
                    recommended_silence_ms=clear_threshold,
                    reason="clear_completed_utterance",
                )
            return EndpointObservation(
                decision="continue_listening",
                confidence=0.72,
                recommended_silence_ms=clear_threshold,
                reason="awaiting_terminal_silence",
            )

        # 6. Neutral utterance without terminal punctuation.
        neutral_threshold = self._threshold(self._neutral_silence_ms, mode)
        if silence_ms >= neutral_threshold:
            return EndpointObservation(
                decision="endpoint_ready",
                confidence=0.8,
                recommended_silence_ms=neutral_threshold,
                reason="silence_threshold_reached",
            )
        return EndpointObservation(
            decision="continue_listening",
            confidence=0.7,
            recommended_silence_ms=neutral_threshold,
            reason="awaiting_endpoint_silence",
        )


class ObservationRegistry:
    """Thread-safe ephemeral registry of concurrent per-(tenant_id, session_id) observations.

    Strict epoch authority rule:
    - ``speech_epoch`` is exclusively gateway-issued.
    - Presenting a ``speech_epoch`` lower than the recorded epoch raises
      ``StaleObservationEpoch`` and leaves state untouched.
    - Presenting a higher ``speech_epoch`` adopts the gateway-issued value.
    - The registry never increments ``speech_epoch`` on its own.
    """

    def __init__(self, evaluator: EndpointEvaluator | None = None) -> None:
        self._lock = threading.Lock()
        self._observations: dict[tuple[str, str], SessionObservation] = {}
        self._evaluator = evaluator or EndpointEvaluator()

    def get(self, tenant_id: str, session_id: str) -> SessionObservation:
        with self._lock:
            entry = self._observations.get((tenant_id, session_id))
            if entry is None:
                raise ObservationSessionNotFound(f"{tenant_id}:{session_id}")
            return replace(entry)

    def observe(
        self,
        *,
        tenant_id: str,
        session_id: str,
        speech_epoch: int,
        capture_id: str | None = None,
        connection_state: ConnectionState | None = None,
        input_state: InputState | None = None,
        reasoning_state: ReasoningState | None = None,
        output_state: OutputState | None = None,
        silence_ms: int | None = None,
        speech_duration_ms: int | None = None,
        transcript: str | None = None,
        stable_text: str | None = None,
        unstable_text: str | None = None,
        transcript_revision: int | None = None,
        assistant_speaking: bool | None = None,
        endpointing_mode: EndpointingMode = "adaptive",
        evaluate_endpoint: bool = True,
    ) -> tuple[SessionObservation, EndpointObservation | None]:
        key = (tenant_id, session_id)
        with self._lock:
            entry = self._observations.get(key)
            if entry is None:
                entry = SessionObservation(
                    tenant_id=tenant_id,
                    session_id=session_id,
                    speech_epoch=speech_epoch,
                    capture_id=capture_id,
                )
                self._observations[key] = entry
            else:
                if speech_epoch < entry.speech_epoch:
                    raise StaleObservationEpoch(entry.speech_epoch, speech_epoch)
                if speech_epoch > entry.speech_epoch:
                    # Adopt the gateway-issued epoch; reset capture transcript tracking.
                    entry.speech_epoch = speech_epoch
                    entry.capture_id = capture_id
                    entry.last_transcript_revision = None
                    entry.last_transcript_text = ""
                    entry.stable_text = ""
                    entry.unstable_text = ""
                elif capture_id is not None and capture_id != entry.capture_id:
                    entry.capture_id = capture_id
                    entry.last_transcript_revision = None
                    entry.last_transcript_text = ""
                    entry.stable_text = ""
                    entry.unstable_text = ""

            prev_revision = entry.last_transcript_revision
            prev_transcript = entry.last_transcript_text

            if connection_state is not None:
                entry.connection_state = connection_state
            if input_state is not None:
                entry.input_state = input_state
            if reasoning_state is not None:
                entry.reasoning_state = reasoning_state
            if output_state is not None:
                entry.output_state = output_state

            effective_transcript = _compose_transcript(transcript, stable_text, unstable_text)
            if transcript is not None or stable_text is not None or unstable_text is not None:
                entry.last_transcript_text = effective_transcript
            if stable_text is not None:
                entry.stable_text = stable_text
            if unstable_text is not None:
                entry.unstable_text = unstable_text
            if transcript_revision is not None:
                entry.last_transcript_revision = transcript_revision

            entry.updated_at = time.time()

            is_assistant_speaking = (
                assistant_speaking
                if assistant_speaking is not None
                else entry.output_state in _ASSISTANT_SPEAKING_STATES
            )
            snapshot = replace(entry)

        endpoint_observation: EndpointObservation | None = None
        if evaluate_endpoint and silence_ms is not None:
            endpoint_observation = self._evaluator.evaluate(
                silence_ms=silence_ms,
                transcript=transcript if transcript is not None else snapshot.last_transcript_text,
                stable_text=stable_text,
                unstable_text=unstable_text,
                transcript_revision=transcript_revision,
                previous_revision=prev_revision,
                previous_transcript=prev_transcript if prev_revision is not None else None,
                speech_duration_ms=speech_duration_ms,
                assistant_speaking=is_assistant_speaking,
                mode=endpointing_mode,
            )

        return snapshot, endpoint_observation
