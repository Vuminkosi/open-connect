"""Local Kokoro v1.0 CPU synthesis engine with buffered and incremental streaming modes."""

from __future__ import annotations

import asyncio
import base64
from collections import OrderedDict
from collections.abc import AsyncIterator, Iterator
import hashlib
import io
import math
import os
from pathlib import Path
import re
import tempfile
import threading
import time
import urllib.request
import wave

import numpy as np

from openconnect_kokoro.config import (
    DEFAULT_INFERENCE_WAIT_TIMEOUT_SECONDS,
    DEFAULT_LOCALE,
    DEFAULT_MAX_PENDING_STREAMS,
    DEFAULT_VOICE_ID,
    VERIFIED_VOICES,
    VerifiedVoice,
    get_verified_voice,
)

NATIVE_SAMPLE_RATE_HZ = 24000
NATIVE_CHANNELS = 1
MAX_TEXT_LENGTH = 2000
MAX_DURATION_SECONDS = 120
MAX_SAMPLES = NATIVE_SAMPLE_RATE_HZ * MAX_DURATION_SECONDS
MODEL_ID = "hexgrad/Kokoro-82M"
MODEL_SHA256 = "496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4"
CONFIG_SHA256 = "5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f"
VOICE_SHA256 = VERIFIED_VOICES[DEFAULT_VOICE_ID].sha256
ARTIFACT_URLS: dict[str, tuple[str, str]] = {
    "kokoro-v1_0.pth": ("https://huggingface.co/hexgrad/Kokoro-82M/resolve/main/kokoro-v1_0.pth", MODEL_SHA256),
    "config.json": ("https://huggingface.co/hexgrad/Kokoro-82M/resolve/main/config.json", CONFIG_SHA256),
    **{
        voice.artifact_relative_path: (voice.artifact_url, voice.sha256)
        for voice in VERIFIED_VOICES.values()
    },
}

_MARKUP_TAG_RE = re.compile(r"</?[a-zA-Z!][^>]*>|<[^>]*$|<>|</>")
_CONTROL_CHAR_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
_SURROGATE_RE = re.compile(r"[\ud800-\udfff]")
_SOFT_LIMIT_THRESHOLD = 0.95
_SOFT_LIMIT_HEADROOM = 0.05
_DECLICK_RAMP_SAMPLES = 16
_DECLICK_STEP_THRESHOLD = 0.05


class VoiceUnknown(Exception):
    pass


class ModelUnavailable(Exception):
    pass


class IdempotencyConflict(Exception):
    pass


class OverloadError(Exception):
    pass


def validate_speech_text(text: str) -> str:
    """Validate text length, characters, and reject malformed markup."""
    if not isinstance(text, str) or not text.strip() or len(text) > MAX_TEXT_LENGTH:
        raise ValueError(f"speech text must contain 1-{MAX_TEXT_LENGTH} characters")
    if _CONTROL_CHAR_RE.search(text) or _SURROGATE_RE.search(text):
        raise ValueError("speech text contains invalid control characters")
    if _MARKUP_TAG_RE.search(text):
        raise ValueError("malformed markup is not allowed in speech text")
    return text


def resolve_verified_voice(
    voice_id: str,
    locale: str | None = None,
    speed: float | None = None,
    *,
    strict_locale: bool = True,
) -> tuple[VerifiedVoice, str, float]:
    """Resolve and validate a verified voice, locale, and speed."""
    voice = get_verified_voice(voice_id)
    if voice is None:
        raise VoiceUnknown(f"unknown voice {voice_id!r}")

    resolved_speed = voice.default_speed if speed is None else speed
    if isinstance(resolved_speed, bool) or not isinstance(resolved_speed, (int, float)):
        raise ValueError("speed must be a finite number")
    resolved_speed_float = float(resolved_speed)
    if not math.isfinite(resolved_speed_float) or not (
        voice.min_speed <= resolved_speed_float <= voice.max_speed
    ):
        raise ValueError(f"speed must be between {voice.min_speed} and {voice.max_speed}")

    if locale is None or not isinstance(locale, str) or not locale.strip():
        if strict_locale:
            raise ValueError("locale is required")
        resolved_locale = voice.locale
    else:
        normalized_locale = locale.strip()
        if strict_locale and normalized_locale.lower() != voice.locale.lower():
            raise ValueError(f"unsupported locale {locale!r} for voice {voice.voice_id!r}")
        resolved_locale = voice.locale if strict_locale else normalized_locale

    return voice, resolved_locale, resolved_speed_float


def _verify(path: Path, expected: str) -> None:
    if not path.is_file():
        raise ModelUnavailable(f"missing Kokoro artifact: {path.name}")
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != expected:
        raise ModelUnavailable(f"Kokoro artifact hash mismatch: {path.name}")


def _ensure_artifacts(directory: Path) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for relative, (url, expected) in ARTIFACT_URLS.items():
        target = directory / relative
        if target.is_file():
            _verify(target, expected)
            continue
        if os.environ.get("OC_KOKORO_ALLOW_DOWNLOAD", "false").lower() != "true":
            raise ModelUnavailable(f"missing Kokoro artifact: {relative}; automatic model download is disabled")
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            with urllib.request.urlopen(url, timeout=120) as response:
                with tempfile.NamedTemporaryFile(dir=target.parent, delete=False) as temporary:
                    while chunk := response.read(1024 * 1024):
                        temporary.write(chunk)
                    temporary_path = Path(temporary.name)
            temporary_path.replace(target)
            _verify(target, expected)
        except Exception as exception:
            target.unlink(missing_ok=True)
            raise ModelUnavailable(f"unable to provision Kokoro artifact: {relative}") from exception


def _normalize_chunk(samples: np.ndarray, prev_sample: float | None) -> tuple[np.ndarray, float]:
    """Apply consistent soft-limiting and boundary de-clicking without altering sample count."""
    chunk = np.array(samples, dtype=np.float32, copy=True)
    abs_chunk = np.abs(chunk)
    hot_mask = abs_chunk > _SOFT_LIMIT_THRESHOLD
    if np.any(hot_mask):
        hot = abs_chunk[hot_mask]
        limited = _SOFT_LIMIT_THRESHOLD + _SOFT_LIMIT_HEADROOM * np.tanh(
            (hot - _SOFT_LIMIT_THRESHOLD) / _SOFT_LIMIT_HEADROOM
        )
        chunk[hot_mask] = np.sign(chunk[hot_mask]) * limited

    if prev_sample is not None and chunk.size > 0:
        if abs(float(chunk[0]) - prev_sample) > _DECLICK_STEP_THRESHOLD:
            ramp_len = min(_DECLICK_RAMP_SAMPLES, chunk.size)
            alpha = np.linspace(0.0, 1.0, ramp_len, endpoint=False, dtype=np.float32)
            chunk[:ramp_len] = prev_sample * (1.0 - alpha) + chunk[:ramp_len] * alpha

    return chunk, float(chunk[-1])


def _pcm16le(samples: np.ndarray) -> bytes:
    clipped = np.clip(samples, -1.0, 1.0)
    return (clipped * 32767.0).astype("<i2", copy=False).tobytes()


def _wav(samples: np.ndarray) -> bytes:
    pcm = _pcm16le(samples)
    stream = io.BytesIO()
    with wave.open(stream, "wb") as output:
        output.setnchannels(NATIVE_CHANNELS)
        output.setsampwidth(2)
        output.setframerate(NATIVE_SAMPLE_RATE_HZ)
        output.writeframes(pcm)
    return stream.getvalue()


def _extract_mono_chunk(audio: object) -> np.ndarray:
    """Convert a model audio output to a validated finite 1D numpy array."""
    if hasattr(audio, "detach"):
        chunk = audio.detach().cpu().numpy()  # type: ignore[union-attr]
    else:
        chunk = np.asarray(audio)
    if chunk.ndim != 1 or not np.isfinite(chunk).all():
        raise ModelUnavailable("Kokoro produced an invalid waveform")
    return chunk


class KokoroEngine:
    """One lazily loaded, CPU-only Kokoro model with bounded replay storage and streaming."""

    engine = "kokoro"
    version = "kokoro==0.9.4"

    def __init__(
        self,
        model_dir: str,
        max_idempotency_records: int = 512,
        max_pending_streams: int = DEFAULT_MAX_PENDING_STREAMS,
        inference_wait_timeout_seconds: float = DEFAULT_INFERENCE_WAIT_TIMEOUT_SECONDS,
    ) -> None:
        self._directory = Path(model_dir)
        self._lock = threading.Lock()
        self._inference_lock = threading.Lock()
        self._waiters_lock = threading.Lock()
        self._pending_streams = 0
        self._max_pending_streams = max(1, int(max_pending_streams))
        self._inference_wait_timeout_seconds = max(0.01, float(inference_wait_timeout_seconds))
        self._idempotency: OrderedDict[str, dict[str, object]] = OrderedDict()
        self._max_records = max_idempotency_records
        self._model = None
        self._pipeline = None
        self._voice_file: Path | None = None
        self._voice_files: dict[str, Path] = {}

    def _resolve_verified_voice_path(self, voice: VerifiedVoice) -> Path:
        root = self._directory.resolve()
        candidate = (self._directory / voice.artifact_relative_path).resolve()
        try:
            candidate.relative_to(root)
        except ValueError as exc:
            raise VoiceUnknown(f"unverified voice asset path for {voice.voice_id!r}") from exc
        _verify(candidate, voice.sha256)
        return candidate

    def _load(self) -> None:
        if self._pipeline is not None:
            return
        _ensure_artifacts(self._directory)
        model_file = self._directory / "kokoro-v1_0.pth"
        config_file = self._directory / "config.json"
        _verify(model_file, MODEL_SHA256)
        _verify(config_file, CONFIG_SHA256)
        verified_paths: dict[str, Path] = {}
        for voice_id, voice in VERIFIED_VOICES.items():
            verified_paths[voice_id] = self._resolve_verified_voice_path(voice)
        try:
            import spacy.util
            from kokoro import KModel, KPipeline
        except Exception as exc:
            raise ModelUnavailable("Kokoro runtime dependencies are unavailable") from exc
        if not spacy.util.is_package("en_core_web_sm"):
            raise ModelUnavailable("en_core_web_sm is unavailable")
        self._model = KModel(
            repo_id=MODEL_ID, config=str(config_file), model=str(model_file)
        ).to("cpu").eval()
        self._pipeline = KPipeline(
            lang_code="a", repo_id=MODEL_ID, model=self._model, trf=False, device="cpu"
        )
        self._voice_files = verified_paths
        self._voice_file = verified_paths[DEFAULT_VOICE_ID]

    def ready(self) -> None:
        with self._lock:
            self._load()

    def validate_stream_request(
        self,
        text: str,
        voice_id: str = DEFAULT_VOICE_ID,
        locale: str = DEFAULT_LOCALE,
        speed: float | None = None,
    ) -> tuple[VerifiedVoice, str, float]:
        """Validate streaming synthesis inputs against the verified voice registry and bounds."""
        validate_speech_text(text)
        return resolve_verified_voice(voice_id, locale=locale, speed=speed, strict_locale=True)

    def synthesize(
        self,
        text: str,
        voice_id: str,
        locale: str,
        audio_format: str,
        idempotency_key: str | None,
    ) -> tuple[dict[str, object], bool]:
        voice, _, resolved_speed = resolve_verified_voice(
            voice_id, locale=locale, speed=None, strict_locale=False
        )
        if audio_format == "mp3":
            raise ValueError("mp3 output is not supported by the local PCM worker")
        if not text.strip() or len(text) > MAX_TEXT_LENGTH:
            raise ValueError(f"speech text must contain 1-{MAX_TEXT_LENGTH} characters")
        if idempotency_key:
            with self._lock:
                cached = self._idempotency.get(idempotency_key)
                if cached is not None:
                    if cached["text"] != text or cached["voiceId"] != voice_id or cached["format"] != audio_format:
                        raise IdempotencyConflict("idempotency key already used")
                    self._idempotency.move_to_end(idempotency_key)
                    return dict(cached["response"]), True  # type: ignore[arg-type]
        with self._inference_lock:
            self.ready()
            import torch
            chunks: list[np.ndarray] = []
            sample_count = 0
            voice_path = self._voice_files.get(voice.voice_id) or self._voice_file
            assert self._pipeline is not None and voice_path is not None
            with torch.inference_mode():
                for result in self._pipeline(text, voice=str(voice_path), speed=resolved_speed):
                    if result.audio is None:
                        continue
                    chunk = _extract_mono_chunk(result.audio)
                    sample_count += chunk.size
                    if sample_count > MAX_SAMPLES:
                        raise ValueError(f"speech duration exceeds {MAX_DURATION_SECONDS} seconds")
                    chunks.append(chunk)
        if not chunks:
            raise ModelUnavailable("Kokoro produced no audio")
        samples = np.concatenate(chunks).astype(np.float32, copy=False)
        audio = _pcm16le(samples) if audio_format == "pcm" else _wav(samples)
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
                    "text": text, "voiceId": voice_id, "format": audio_format, "response": dict(response)
                }
                while len(self._idempotency) > self._max_records:
                    self._idempotency.popitem(last=False)
        return response, False

    def _acquire_stream_slot(self, cancel_event: threading.Event | None) -> bool:
        """Reserve a bounded pending slot and acquire the inference lock with timeout/cancel."""
        if cancel_event is not None and cancel_event.is_set():
            return False

        with self._waiters_lock:
            if self._pending_streams >= self._max_pending_streams:
                raise OverloadError("synthesis queue limit exceeded")
            self._pending_streams += 1

        acquired = False
        try:
            deadline = time.monotonic() + self._inference_wait_timeout_seconds
            while True:
                if cancel_event is not None and cancel_event.is_set():
                    return False
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise OverloadError("synthesis inference wait timeout exceeded")
                if self._inference_lock.acquire(timeout=min(0.02, remaining)):
                    acquired = True
                    if cancel_event is not None and cancel_event.is_set():
                        self._inference_lock.release()
                        acquired = False
                        return False
                    return True
        finally:
            if not acquired:
                with self._waiters_lock:
                    self._pending_streams = max(0, self._pending_streams - 1)

    def _release_stream_slot(self) -> None:
        try:
            self._inference_lock.release()
        finally:
            with self._waiters_lock:
                self._pending_streams = max(0, self._pending_streams - 1)

    def synthesize_stream(
        self,
        text: str,
        voice_id: str = DEFAULT_VOICE_ID,
        locale: str = DEFAULT_LOCALE,
        speed: float | None = None,
        cancel_event: threading.Event | None = None,
    ) -> Iterator[bytes]:
        """Incrementally synthesize validated 24 kHz mono PCM16LE chunks without caching."""
        voice, _, resolved_speed = self.validate_stream_request(
            text=text, voice_id=voice_id, locale=locale, speed=speed
        )
        if cancel_event is not None and cancel_event.is_set():
            return

        if not self._acquire_stream_slot(cancel_event):
            return

        try:
            if cancel_event is not None and cancel_event.is_set():
                return
            self.ready()
            if cancel_event is not None and cancel_event.is_set():
                return

            voice_path = self._voice_files.get(voice.voice_id)
            if voice_path is None and voice.voice_id == DEFAULT_VOICE_ID:
                voice_path = self._voice_file
            if self._pipeline is None or voice_path is None:
                raise ModelUnavailable("Kokoro pipeline or verified voice is unavailable")

            import torch

            sample_count = 0
            prev_sample: float | None = None
            with torch.inference_mode():
                for result in self._pipeline(text, voice=str(voice_path), speed=resolved_speed):
                    if cancel_event is not None and cancel_event.is_set():
                        return
                    if result.audio is None:
                        continue
                    chunk = _extract_mono_chunk(result.audio)
                    if chunk.size == 0:
                        continue
                    sample_count += chunk.size
                    if sample_count > MAX_SAMPLES:
                        raise ValueError(f"speech duration exceeds {MAX_DURATION_SECONDS} seconds")
                    normalized, prev_sample = _normalize_chunk(chunk, prev_sample)
                    pcm = _pcm16le(normalized)
                    if cancel_event is not None and cancel_event.is_set():
                        return
                    yield pcm

            if (cancel_event is None or not cancel_event.is_set()) and sample_count == 0:
                raise ModelUnavailable("Kokoro produced no audio")
        finally:
            self._release_stream_slot()

    async def synthesize_stream_async(
        self,
        text: str,
        voice_id: str = DEFAULT_VOICE_ID,
        locale: str = DEFAULT_LOCALE,
        speed: float | None = None,
        cancel_event: threading.Event | None = None,
    ) -> AsyncIterator[bytes]:
        """Async wrapper that streams PCM16LE chunks off a worker thread without blocking the loop."""
        self.validate_stream_request(text=text, voice_id=voice_id, locale=locale, speed=speed)
        effective_cancel = cancel_event if cancel_event is not None else threading.Event()
        if effective_cancel.is_set():
            return

        loop = asyncio.get_running_loop()
        queue: asyncio.Queue[tuple[str, bytes | Exception | None]] = asyncio.Queue()
        backpressure = threading.Semaphore(4)

        def _producer() -> None:
            try:
                for pcm in self.synthesize_stream(
                    text=text,
                    voice_id=voice_id,
                    locale=locale,
                    speed=speed,
                    cancel_event=effective_cancel,
                ):
                    while not backpressure.acquire(timeout=0.02):
                        if effective_cancel.is_set():
                            return
                    if effective_cancel.is_set():
                        backpressure.release()
                        return
                    loop.call_soon_threadsafe(queue.put_nowait, ("chunk", pcm))
            except Exception as exc:  # noqa: BLE001
                loop.call_soon_threadsafe(queue.put_nowait, ("error", exc))
            else:
                loop.call_soon_threadsafe(queue.put_nowait, ("done", None))

        worker = threading.Thread(target=_producer, name="kokoro-stream", daemon=True)
        worker.start()

        finished_normally = False
        try:
            while True:
                kind, payload = await queue.get()
                if kind == "chunk":
                    backpressure.release()
                    if effective_cancel.is_set():
                        return
                    assert isinstance(payload, bytes)
                    yield payload
                elif kind == "error":
                    finished_normally = True
                    assert isinstance(payload, Exception)
                    raise payload
                else:
                    finished_normally = True
                    return
        finally:
            if not finished_normally:
                effective_cancel.set()
            backpressure.release()
            await asyncio.to_thread(worker.join, 5.0)
