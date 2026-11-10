"""Voice session state machine with speech-epoch fencing.

The worker is never the session source of truth: sessions live only in this
process-local ephemeral registry (no persistence, no replication). The host
session service owns authoritative session state; this registry exists solely
to fence stale audio work (barge-in) via monotonically increasing speech
epochs.
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field

TERMINAL_STATES = ("cancelled", "failed")

# Event -> states the event may be applied in.
_ALLOWED: dict[str, frozenset[str]] = {
    "begin-listening": frozenset({"idle", "interrupted"}),
    "vad-speech-start": frozenset({"listening"}),
    "transcript-partial": frozenset({"listening"}),
    "transcript-final": frozenset({"listening", "transcribing"}),
    "begin-reasoning": frozenset({"transcribing"}),
    "begin-speaking": frozenset({"reasoning"}),
    "utterance-complete": frozenset({"speaking"}),
    "barge-in": frozenset({"listening", "transcribing", "reasoning", "speaking"}),
    "resume-listening": frozenset({"interrupted"}),
    "cancel": frozenset(
        {"idle", "listening", "transcribing", "reasoning", "speaking", "interrupted"}
    ),
    "fail": frozenset(
        {"idle", "listening", "transcribing", "reasoning", "speaking", "interrupted"}
    ),
}

# Events that must present the current speech epoch to be accepted.
_EPOCH_FENCED = frozenset({"begin-speaking", "utterance-complete"})


class SessionNotFound(Exception):
    pass


class InvalidTransition(Exception):
    def __init__(self, event: str, state: str) -> None:
        super().__init__(f"event {event!r} not allowed in state {state!r}")
        self.event = event
        self.state = state


class StaleEpoch(Exception):
    def __init__(self, expected: int, presented: int | None) -> None:
        super().__init__(f"stale speech epoch: expected {expected}, got {presented}")
        self.expected = expected
        self.presented = presented


@dataclass
class VoiceSession:
    session_id: str
    state: str = "idle"
    speech_epoch: int = 0
    transcript: list[str] = field(default_factory=list)
    updated_at: float = field(default_factory=time.time)


class SessionRegistry:
    """Ephemeral, process-local voice session registry."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._sessions: dict[str, VoiceSession] = {}

    def open(self, session_id: str) -> VoiceSession:
        with self._lock:
            existing = self._sessions.get(session_id)
            if existing is not None:
                return existing
            session = VoiceSession(session_id=session_id)
            self._sessions[session_id] = session
            return session

    def get(self, session_id: str) -> VoiceSession:
        with self._lock:
            session = self._sessions.get(session_id)
            if session is None:
                raise SessionNotFound(session_id)
            return session

    def apply_event(
        self,
        session_id: str,
        event: str,
        speech_epoch: int | None = None,
        text: str | None = None,
    ) -> tuple[VoiceSession, dict[str, object]]:
        """Apply an event; returns (session, outcome detail).

        Raises SessionNotFound, InvalidTransition, StaleEpoch.
        Stale-epoch audio work is discarded, never applied.
        """
        with self._lock:
            session = self._sessions.get(session_id)
            if session is None:
                raise SessionNotFound(session_id)
            if event not in _ALLOWED:
                raise InvalidTransition(event, session.state)
            if session.state not in _ALLOWED[event]:
                raise InvalidTransition(event, session.state)
            if event in _EPOCH_FENCED and speech_epoch != session.speech_epoch:
                raise StaleEpoch(session.speech_epoch, speech_epoch)

            detail: dict[str, object] = {}
            if event == "barge-in":
                # Barge-in invalidates in-flight audio: bump the epoch so any
                # stale-epoch chunk or completion is discarded on arrival.
                session.speech_epoch += 1
                session.state = "interrupted"
                detail = {
                    "discardedEpoch": session.speech_epoch - 1,
                    "speechEpoch": session.speech_epoch,
                }
            elif event == "begin-listening":
                session.state = "listening"
            elif event == "vad-speech-start":
                detail = {"speechEpoch": session.speech_epoch}
            elif event in {"transcript-partial", "transcript-final"}:
                session.state = "transcribing"
                if text:
                    session.transcript.append(text)
            elif event == "begin-reasoning":
                session.state = "reasoning"
            elif event == "begin-speaking":
                session.state = "speaking"
                detail = {"speechEpoch": session.speech_epoch}
            elif event == "utterance-complete":
                session.state = "listening"
            elif event == "resume-listening":
                session.state = "listening"
            elif event == "cancel":
                session.state = "cancelled"
            elif event == "fail":
                session.state = "failed"
            session.updated_at = time.time()
            return session, detail
