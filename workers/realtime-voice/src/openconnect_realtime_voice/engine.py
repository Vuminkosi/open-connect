"""Fake orchestration engine: deterministic, no local model weights required."""

from __future__ import annotations

from openconnect_realtime_voice.sessions import SessionRegistry


class FakeVoiceEngine:
    """Produces deterministic orchestration decisions for tests and wiring.

    In orchestration-only mode there are no local weights to initialize; this
    engine stands in for the STT/reasoning/TTS pipeline so hosts can verify
    session flow, epoch fencing, and chunk contracts end to end.
    """

    engine = "fake-engine"
    version = "0.1.0"

    def __init__(self, sessions: SessionRegistry) -> None:
        self._sessions = sessions

    def synthesize_chunks(self, text: str, voice: str, language: str) -> list[dict[str, object]]:
        """Split text into fake audio chunks matching contracts/workers/v1/speech-chunk.json."""
        """Split text into speech-chunk-contract shaped fake audio chunks."""
        words = text.split()
        chunks: list[dict[str, object]] = []
        step = 20
        parts = [" ".join(words[i : i + step]) for i in range(0, len(words), step)] or [""]
        for index, part in enumerate(parts):
            chunks.append(
                {
                    "text": part[:1500],
                    "sequence": index,
                    "isFinal": index == len(parts) - 1,
                    "voice": voice,
                    "language": language,
                }
            )
        return chunks
