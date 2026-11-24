"""Persistent multi-utterance local streaming STT using bounded rolling windows.

This module provides a streaming recognizer abstraction over the verified local
Whisper engine. It uses bounded rolling-window periodic decodes with
stable-prefix/unstable-suffix reconciliation and explicit per-utterance
finalization and reset. Rolling Whisper is NOT native streaming and is never
advertised as such.
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import uuid
from dataclasses import dataclass, field
from typing import Any, Protocol

from fastapi import WebSocket, WebSocketDisconnect

from openconnect_transcription import realtime_local
from openconnect_transcription.config import Settings
from openconnect_transcription.engine import local_whisper
from openconnect_transcription.engine.audio import ValidatedAudio

logger = logging.getLogger("openconnect_transcription")

SAMPLE_RATE_HZ = realtime_local._SAMPLE_RATE
CHANNELS = 1
BITS_PER_SAMPLE = 16
BYTES_PER_SECOND = realtime_local._BYTES_PER_SECOND
MIN_AUDIO_BYTES = BYTES_PER_SECOND // 1000  # 1 ms = 48 bytes at 24 kHz PCM16
MAX_FRAME_BYTES = realtime_local._MAX_FRAME  # 96 KiB
MAX_UTTERANCE_BYTES = realtime_local._MAX_CAPTURE_BYTES  # 32 MiB
MAX_UTTERANCE_MS = realtime_local._MAX_CAPTURE_MS  # 120 s
MAX_TRANSCRIPT_BYTES = MAX_FRAME_BYTES // 2
DEFAULT_PARTIAL_CADENCE_MS = 400
DEFAULT_ROLLING_WINDOW_MS = 15_000
MIN_PARTIAL_AUDIO_MS = 100
IDLE_SESSION_TIMEOUT_S = 300.0
_SILENCE_PEAK_THRESHOLD = 16  # ~-66 dBFS for signed 16-bit PCM
_LOCALE_RE = re.compile(r"^[a-z]{2}(-[A-Z]{2})?$")


def is_silence_pcm16(pcm: bytes, threshold: int = _SILENCE_PEAK_THRESHOLD) -> bool:
    """Return True if PCM16LE buffer is shorter than 1 ms or within digital silence."""
    if len(pcm) < MIN_AUDIO_BYTES:
        return True
    if len(pcm) % 2 != 0:
        return True
    samples = memoryview(pcm).cast("h")
    return max(samples) <= threshold and min(samples) >= -threshold


def _normalize_text(text: str) -> str:
    return " ".join(text.split())


@dataclass(frozen=True)
class PartialHypothesis:
    revision: int
    text: str
    stable_text: str
    unstable_text: str


@dataclass(frozen=True)
class FinalTranscript:
    revision: int
    text: str
    is_empty: bool


class TranscriptReconciler:
    """Reconciles consecutive rolling-window hypotheses into stable/unstable spans.

    Revisions are monotonically increasing per capture and may replace earlier
    hypotheses when the model revises prior words (never treated as append-only).
    """

    def __init__(self) -> None:
        self._revision = 0
        self._prev_words: list[str] = []
        self._last_emitted: tuple[str, str, str] | None = None

    @property
    def revision(self) -> int:
        return self._revision

    def reconcile_partial(self, raw_text: str) -> PartialHypothesis | None:
        normalized = _normalize_text(raw_text)
        if not normalized:
            return None
        words = normalized.split(" ")
        lcp = 0
        for prev_word, cur_word in zip(self._prev_words, words):
            if prev_word == cur_word:
                lcp += 1
            else:
                break
        stable_text = " ".join(words[:lcp])
        unstable_text = " ".join(words[lcp:])
        self._prev_words = words
        candidate = (normalized, stable_text, unstable_text)
        if candidate == self._last_emitted:
            return None
        self._revision += 1
        self._last_emitted = candidate
        return PartialHypothesis(
            revision=self._revision,
            text=normalized,
            stable_text=stable_text,
            unstable_text=unstable_text,
        )

    def finalize(self, raw_text: str) -> FinalTranscript:
        normalized = _normalize_text(raw_text)
        self._revision += 1
        return FinalTranscript(
            revision=self._revision,
            text=normalized,
            is_empty=(normalized == ""),
        )

    def reset(self) -> None:
        self._revision = 0
        self._prev_words.clear()
        self._last_emitted = None


class StreamingLocalRecognizer(Protocol):
    """Abstraction for local streaming recognizers (rolling Whisper or future native)."""

    recognition_mode: str
    native_streaming: bool
    supports_partials: bool
    supports_finalization: bool
    supports_reset: bool
    sample_rate_hz: int
    channels: int

    def decode_partial(self, model: Any, pcm_bytes: bytes, locale: str, window_ms: int) -> str: ...

    def decode_final(self, model: Any, pcm_bytes: bytes, locale: str) -> str: ...


class RollingWindowLocalRecognizer:
    """Bounded rolling-window local recognizer backed by verified local Whisper.

    Never claims to be native streaming; decodes a bounded trailing window for
    interim hypotheses and reconciles against the full utterance buffer on end.
    """

    recognition_mode = "rolling-window"
    native_streaming = False
    supports_partials = True
    supports_finalization = True
    supports_reset = True
    sample_rate_hz = SAMPLE_RATE_HZ
    channels = CHANNELS

    def decode_partial(self, model: Any, pcm_bytes: bytes, locale: str, window_ms: int) -> str:
        window_bytes = max(MIN_AUDIO_BYTES, (window_ms * BYTES_PER_SECOND // 1000) & ~1)
        window = pcm_bytes[-window_bytes:] if len(pcm_bytes) > window_bytes else pcm_bytes
        if is_silence_pcm16(window):
            return ""
        duration_ms = max(1, len(window) * 1000 // BYTES_PER_SECOND)
        validated = ValidatedAudio(
            window, "audio/pcm", self.sample_rate_hz, self.channels, BITS_PER_SAMPLE, duration_ms
        )
        result = local_whisper.transcribe(model, validated, locale)
        return _normalize_text(result.transcript)

    def decode_final(self, model: Any, pcm_bytes: bytes, locale: str) -> str:
        if is_silence_pcm16(pcm_bytes):
            return ""
        duration_ms = max(1, len(pcm_bytes) * 1000 // BYTES_PER_SECOND)
        validated = ValidatedAudio(
            pcm_bytes,
            "audio/pcm",
            self.sample_rate_hz,
            self.channels,
            BITS_PER_SAMPLE,
            duration_ms,
        )
        result = local_whisper.transcribe(model, validated, locale)
        return _normalize_text(result.transcript)


@dataclass
class _UtteranceState:
    capture_id: str
    speech_epoch: int
    locale: str
    generation: int
    audio: bytearray = field(default_factory=bytearray)
    reconciler: TranscriptReconciler = field(default_factory=TranscriptReconciler)
    last_partial_bytes: int = 0
    last_partial_at: float = 0.0
    explicit: bool = True

    def wipe(self) -> None:
        if self.audio:
            self.audio[:] = b"\x00" * len(self.audio)
            self.audio.clear()


def _parse_capture_id(value: Any, *, required: bool) -> str | None:
    if value is None:
        return None if not required else ""
    if not isinstance(value, str):
        return ""
    stripped = value.strip()
    if not stripped or len(stripped) > 128:
        return ""
    return stripped


def _parse_speech_epoch(value: Any, *, default: int | None = None) -> int | None:
    if value is None:
        return default
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        return None
    return value


def _parse_locale(value: Any, default: str) -> str | None:
    if value is None:
        return default
    if not isinstance(value, str) or not _LOCALE_RE.match(value):
        return None
    return value


async def transcribe_stream(
    websocket: WebSocket,
    settings: Settings,
    *,
    recognizer: StreamingLocalRecognizer | None = None,
    cadence_ms: int | None = None,
    window_ms: int | None = None,
    idle_timeout_s: float = IDLE_SESSION_TIMEOUT_S,
    accept: bool = True,
) -> None:
    """Serve a persistent multi-utterance local streaming STT WebSocket session."""
    if accept:
        await websocket.accept()
    if (
        settings.backend != "local-model"
        or settings.fake_engine
        or not realtime_local._slots.acquire(False)
    ):
        await websocket.close(code=1013, reason="model_unavailable")
        return

    active_recognizer = recognizer or RollingWindowLocalRecognizer()
    effective_cadence_ms = max(
        10,
        cadence_ms if cadence_ms is not None else settings.streaming_partial_cadence_ms,
    )
    effective_window_ms = max(
        500,
        min(
            MAX_UTTERANCE_MS,
            window_ms if window_ms is not None else settings.streaming_window_ms,
        ),
    )
    cadence_s = effective_cadence_ms / 1000.0
    min_partial_bytes = max(MIN_AUDIO_BYTES, MIN_PARTIAL_AUDIO_MS * BYTES_PER_SECOND // 1000)

    utterance: _UtteranceState | None = None
    generation = 0
    session_closed = False
    partial_error: BaseException | None = None
    inflight_decode: asyncio.Task[None] | None = None
    receive_task: asyncio.Task[dict[str, Any]] | None = None
    send_lock = asyncio.Lock()

    async def _send_json(payload: dict[str, Any]) -> None:
        async with send_lock:
            if not session_closed:
                await websocket.send_json(payload)

    async def _drain_inflight() -> None:
        nonlocal inflight_decode
        if inflight_decode is not None:
            try:
                await inflight_decode
            except BaseException:
                pass
            finally:
                inflight_decode = None

    try:
        model = await realtime_local._run_blocking(realtime_local.load_local_model, settings)
        if model is None:
            await websocket.close(code=1013, reason="model_unavailable")
            return

        duration_limit = min(settings.max_duration_ms, MAX_UTTERANCE_MS)
        utterance_limit = min(
            settings.max_audio_bytes,
            MAX_UTTERANCE_BYTES,
            duration_limit * BYTES_PER_SECOND // 1000,
        )
        if utterance_limit < MIN_AUDIO_BYTES:
            await websocket.close(code=1013, reason="model_unavailable")
            return

        await _send_json(
            {
                "type": "sessionReady",
                "version": 2,
                "supportsPartials": active_recognizer.supports_partials,
                "sampleRateHz": active_recognizer.sample_rate_hz,
                "channels": active_recognizer.channels,
            }
        )

        loop = asyncio.get_running_loop()
        last_activity = loop.time()

        async def _run_partial(
            snap_utterance: _UtteranceState,
            snap_gen: int,
            snap_capture: str,
            snap_epoch: int,
            snap_locale: str,
            snap_pcm: bytes,
        ) -> None:
            nonlocal partial_error
            try:
                text = await realtime_local._run_blocking(
                    active_recognizer.decode_partial,
                    model,
                    snap_pcm,
                    snap_locale,
                    effective_window_ms,
                )
            except BaseException as exc:
                if not session_closed and snap_gen == generation:
                    partial_error = exc
                return

            if session_closed or snap_gen != generation:
                return
            hyp = snap_utterance.reconciler.reconcile_partial(text)
            if hyp is None:
                return
            if len(hyp.text.encode("utf-8")) > MAX_TRANSCRIPT_BYTES:
                if not session_closed and snap_gen == generation:
                    partial_error = ValueError("transcript_too_large")
                return
            async with send_lock:
                if session_closed or snap_gen != generation:
                    return
                await websocket.send_json(
                    {
                        "type": "transcript.partial",
                        "captureId": snap_capture,
                        "speechEpoch": snap_epoch,
                        "revision": hyp.revision,
                        "text": hyp.text,
                        "stableText": hyp.stable_text,
                        "unstableText": hyp.unstable_text,
                        "authoritative": False,
                    }
                )

        def _maybe_start_partial(now: float) -> None:
            nonlocal inflight_decode
            if not active_recognizer.supports_partials or utterance is None:
                return
            if inflight_decode is not None and not inflight_decode.done():
                return
            if len(utterance.audio) < min_partial_bytes:
                return
            if len(utterance.audio) <= utterance.last_partial_bytes:
                return
            if (now - utterance.last_partial_at) < cadence_s:
                return
            snap_pcm = bytes(utterance.audio)
            utterance.last_partial_bytes = len(snap_pcm)
            utterance.last_partial_at = now
            inflight_decode = asyncio.create_task(
                _run_partial(
                    utterance,
                    utterance.generation,
                    utterance.capture_id,
                    utterance.speech_epoch,
                    utterance.locale,
                    snap_pcm,
                )
            )

        while True:
            if partial_error is not None:
                raise partial_error

            now = loop.time()
            _maybe_start_partial(now)

            idle_remaining = (last_activity + idle_timeout_s) - now
            if idle_remaining <= 0:
                raise asyncio.TimeoutError

            wait_timeout = idle_remaining
            if (
                active_recognizer.supports_partials
                and utterance is not None
                and len(utterance.audio) >= min_partial_bytes
                and len(utterance.audio) > utterance.last_partial_bytes
                and (inflight_decode is None or inflight_decode.done())
            ):
                next_due = (utterance.last_partial_at + cadence_s) - now
                wait_timeout = max(0.0, min(wait_timeout, next_due))

            if receive_task is None:
                receive_task = asyncio.create_task(websocket.receive())

            wait_set: set[asyncio.Task[Any]] = {receive_task}
            if inflight_decode is not None and not inflight_decode.done():
                wait_set.add(inflight_decode)

            done, _ = await asyncio.wait(
                wait_set,
                timeout=wait_timeout,
                return_when=asyncio.FIRST_COMPLETED,
            )

            if partial_error is not None:
                raise partial_error

            if receive_task not in done:
                continue

            message = receive_task.result()
            receive_task = None
            last_activity = loop.time()

            if message["type"] == "websocket.disconnect":
                return

            payload = message.get("bytes")
            if payload is not None:
                if not payload or len(payload) % 2 != 0 or len(payload) > MAX_FRAME_BYTES:
                    session_closed = True
                    generation += 1
                    await websocket.close(code=1003, reason="invalid_pcm_stream")
                    return
                if utterance is None:
                    generation += 1
                    utterance = _UtteranceState(
                        capture_id=f"cap-{uuid.uuid4().hex[:12]}",
                        speech_epoch=0,
                        locale=settings.default_locale,
                        generation=generation,
                        last_partial_at=loop.time(),
                        explicit=False,
                    )
                if len(utterance.audio) + len(payload) > utterance_limit:
                    session_closed = True
                    generation += 1
                    await websocket.close(code=1003, reason="invalid_pcm_stream")
                    return
                utterance.audio.extend(payload)
                _maybe_start_partial(loop.time())
                continue

            try:
                control = json.loads(message.get("text") or "")
            except (ValueError, TypeError):
                control = None

            if not isinstance(control, dict) or not isinstance(control.get("type"), str):
                session_closed = True
                generation += 1
                await websocket.close(code=1003, reason="invalid_control")
                return

            msg_type = control["type"]

            if msg_type == "startUtterance":
                capture_id = _parse_capture_id(control.get("captureId"), required=True)
                speech_epoch = _parse_speech_epoch(control.get("speechEpoch"), default=0)
                locale = _parse_locale(control.get("locale"), settings.default_locale)
                if not capture_id or speech_epoch is None or locale is None:
                    session_closed = True
                    generation += 1
                    await websocket.close(code=1003, reason="invalid_control")
                    return
                if utterance is not None:
                    utterance.wipe()
                generation += 1
                utterance = _UtteranceState(
                    capture_id=capture_id,
                    speech_epoch=speech_epoch,
                    locale=locale,
                    generation=generation,
                    last_partial_at=loop.time(),
                    explicit=True,
                )
                continue

            if msg_type == "cancelUtterance":
                req_capture = _parse_capture_id(control.get("captureId"), required=False)
                req_epoch = _parse_speech_epoch(control.get("speechEpoch"), default=0)
                if req_capture == "" or req_epoch is None:
                    session_closed = True
                    generation += 1
                    await websocket.close(code=1003, reason="invalid_control")
                    return
                generation += 1
                if utterance is not None:
                    utterance.wipe()
                    utterance = None
                continue

            if msg_type == "endUtterance":
                req_capture = _parse_capture_id(control.get("captureId"), required=False)
                req_epoch = _parse_speech_epoch(control.get("speechEpoch"), default=None)
                if req_capture == "" or (
                    "speechEpoch" in control and req_epoch is None
                ):
                    session_closed = True
                    generation += 1
                    await websocket.close(code=1003, reason="invalid_control")
                    return

                if utterance is not None:
                    if (
                        utterance.explicit
                        and req_capture is not None
                        and req_capture != utterance.capture_id
                    ):
                        session_closed = True
                        generation += 1
                        await websocket.close(code=1003, reason="invalid_control")
                        return
                    capture_id = req_capture or utterance.capture_id
                    speech_epoch = (
                        req_epoch if req_epoch is not None else utterance.speech_epoch
                    )
                    locale = utterance.locale
                    reconciler = utterance.reconciler
                    audio_bytes = bytes(utterance.audio)
                    utterance.wipe()
                    utterance = None
                else:
                    capture_id = req_capture or f"cap-{uuid.uuid4().hex[:12]}"
                    speech_epoch = req_epoch if req_epoch is not None else 0
                    locale = settings.default_locale
                    reconciler = TranscriptReconciler()
                    audio_bytes = b""

                # Invalidate any in-flight partial decode and wait for single-flight completion.
                generation += 1
                await _drain_inflight()

                if is_silence_pcm16(audio_bytes):
                    final_raw = ""
                else:
                    final_raw = await realtime_local._run_blocking(
                        active_recognizer.decode_final,
                        model,
                        audio_bytes,
                        locale,
                    )

                final = reconciler.finalize(final_raw)
                if len(final.text.encode("utf-8")) > MAX_TRANSCRIPT_BYTES:
                    session_closed = True
                    await websocket.close(code=1013, reason="transcription_unavailable")
                    return

                await _send_json(
                    {
                        "type": "transcript.final",
                        "captureId": capture_id,
                        "speechEpoch": speech_epoch,
                        "revision": final.revision,
                        "text": final.text,
                        "isEmpty": final.is_empty,
                        "authoritative": True,
                    }
                )
                continue

            if msg_type == "closeSession":
                session_closed = True
                generation += 1
                if utterance is not None:
                    utterance.wipe()
                    utterance = None
                await websocket.close(code=1000)
                return

            session_closed = True
            generation += 1
            await websocket.close(code=1003, reason="invalid_control")
            return

    except WebSocketDisconnect:
        return
    except asyncio.TimeoutError:
        session_closed = True
        generation += 1
        try:
            await websocket.close(code=1008, reason="session_timeout")
        except Exception:
            pass
    except Exception:
        session_closed = True
        generation += 1
        try:
            await websocket.close(code=1013, reason="transcription_unavailable")
        except Exception:
            pass
    finally:
        session_closed = True
        generation += 1
        if utterance is not None:
            utterance.wipe()
            utterance = None
        if receive_task is not None and not receive_task.done():
            receive_task.cancel()
            try:
                await receive_task
            except (asyncio.CancelledError, WebSocketDisconnect, Exception):
                pass
        # Shielded native inference calls must complete before releasing the capacity slot.
        await _drain_inflight()
        realtime_local._slots.release()
