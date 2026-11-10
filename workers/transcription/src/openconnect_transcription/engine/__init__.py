"""Bounded fake STT engine: deterministic final transcripts, no downloads.

Produces a final transcript plus a revision for validated PCM audio. Partial
(interim) results are NOT supported by this engine: requests that set
``partialsRequested`` receive ``capability_unavailable`` instead of a
fabricated streaming capability.
"""

from __future__ import annotations

import hashlib
from dataclasses import dataclass

from openconnect_transcription.engine.audio import ValidatedAudio
from openconnect_transcription.schemas import ALLOWED_SAMPLE_RATES


@dataclass(frozen=True)
class TranscriptResult:
    transcript: str
    revision: int
    is_final: bool
    locale: str
    duration_ms: int
    segments: list[tuple[int, int, str]]


class FakeTranscriptionEngine:
    """Deterministic bounded fake: final transcript only, no network."""

    name = "fake-bounded"
    performs_downloads = False
    performs_network_calls = False
    supports_partials = False

    def transcribe(self, audio: ValidatedAudio, locale: str) -> TranscriptResult:
        digest = hashlib.sha256(audio.raw_pcm).hexdigest()[:12]
        text = (
            f"fake transcript [{locale}] {audio.duration_ms}ms "
            f"{audio.sample_rate_hz}Hz ch{audio.channels} #{digest}"
        )
        return TranscriptResult(
            transcript=text,
            revision=1,
            is_final=True,
            locale=locale,
            duration_ms=audio.duration_ms,
            segments=[(0, audio.duration_ms, text)],
        )


def advertised_capabilities(*, backend: str, partials_supported: bool = False) -> list[str]:
    """Return exactly the capabilities this engine instance can serve."""
    caps = ["transcribe-final"]
    if backend in ("local-fake", "local-model"):
        caps.append("transcribe-recorded")
    if partials_supported:  # fake engine never sets this today
        caps.append("transcribe-partials")
    return caps


def supported_sample_rates() -> list[int]:
    return sorted(ALLOWED_SAMPLE_RATES)
