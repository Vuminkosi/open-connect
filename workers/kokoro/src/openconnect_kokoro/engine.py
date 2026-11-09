"""Fake Kokoro synthesis engine: deterministic, never downloads weights.

The synthesize path is strictly offline: if KOKORO_REQUIRE_MODEL is set and
the init container has not staged the model marker, synthesis fails closed
with model-unavailable instead of attempting any download.
"""

from __future__ import annotations

import base64
import hashlib
import threading
from collections import OrderedDict

NATIVE_SAMPLE_RATE_HZ = 24000
NATIVE_CHANNELS = 1


class VoiceUnknown(Exception):
    pass


class ModelUnavailable(Exception):
    pass


class IdempotencyConflict(Exception):
    pass


def _fake_audio_bytes(text: str, voice_id: str, audio_format: str) -> bytes:
    """Deterministic placeholder bytes; not real audio, clearly fake-engine output."""
    digest = hashlib.sha256(
        f"fake-kokoro|{voice_id}|{audio_format}|{text}".encode("utf-8")
    ).digest()
    header = f"FAKE-{audio_format.upper()}-24KHZ-MONO|".encode("utf-8")
    return header + digest * 64


class FakeKokoroEngine:
    engine = "fake-engine"
    version = "0.1.0"

    def __init__(self, max_idempotency_records: int = 512) -> None:
        self._lock = threading.Lock()
        self._idempotency: OrderedDict[str, dict[str, object]] = OrderedDict()
        self._max_records = max_idempotency_records

    def synthesize(
        self,
        text: str,
        voice_id: str,
        locale: str,
        audio_format: str,
        voices: tuple[str, ...],
        model_ready: bool,
        require_model: bool,
        idempotency_key: str | None,
    ) -> tuple[dict[str, object], bool]:
        if voice_id not in voices:
            raise VoiceUnknown(f"unknown voice {voice_id!r}")
        if require_model and not model_ready:
            # Fail closed: never download on the synthesize path.
            raise ModelUnavailable("kokoro model not staged; run init-models first")
        if idempotency_key:
            with self._lock:
                cached = self._idempotency.get(idempotency_key)
                if cached is not None:
                    if (
                        cached["text"] != text
                        or cached["voiceId"] != voice_id
                        or cached["format"] != audio_format
                    ):
                        raise IdempotencyConflict("idempotency key already used")
                    self._idempotency.move_to_end(idempotency_key)
                    return dict(cached["response"]), True  # type: ignore[index]
        audio = _fake_audio_bytes(text, voice_id, audio_format)
        response: dict[str, object] = {
            "text": text,
            "voiceId": voice_id,
            "locale": locale,
            "format": audio_format,
            "sampleRateHz": NATIVE_SAMPLE_RATE_HZ,
            "channels": NATIVE_CHANNELS,
            "audioBase64": base64.b64encode(audio).decode("ascii"),
        }
        if idempotency_key:
            with self._lock:
                self._idempotency[idempotency_key] = {
                    "text": text,
                    "voiceId": voice_id,
                    "format": audio_format,
                    "response": dict(response),
                }
                while len(self._idempotency) > self._max_records:
                    self._idempotency.popitem(last=False)
        return response, False
