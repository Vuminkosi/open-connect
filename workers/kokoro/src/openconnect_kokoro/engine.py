"""Local Kokoro v1.0 CPU synthesis engine."""

from __future__ import annotations

import base64
import hashlib
import io
import threading
import wave
from collections import OrderedDict
from pathlib import Path

import numpy as np

NATIVE_SAMPLE_RATE_HZ = 24000
NATIVE_CHANNELS = 1
MODEL_ID = "hexgrad/Kokoro-82M"
MODEL_SHA256 = "496dba118d1a58f5f3db2efc88dbdc216e0483fc89fe6e47ee1f2c53f18ad1e4"
CONFIG_SHA256 = "5abb01e2403b072bf03d04fde160443e209d7a0dad49a423be15196b9b43c17f"
VOICE_SHA256 = "0ab5709b8ffab19bfd849cd11d98f75b60af7733253ad0d67b12382a102cb4ff"


class VoiceUnknown(Exception):
    pass


class ModelUnavailable(Exception):
    pass


class IdempotencyConflict(Exception):
    pass


def _verify(path: Path, expected: str) -> None:
    if not path.is_file():
        raise ModelUnavailable(f"missing Kokoro artifact: {path.name}")
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != expected:
        raise ModelUnavailable(f"Kokoro artifact hash mismatch: {path.name}")


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


class KokoroEngine:
    """One lazily loaded, CPU-only Kokoro model with bounded replay storage."""

    engine = "kokoro"
    version = "kokoro==0.9.4"

    def __init__(self, model_dir: str, max_idempotency_records: int = 512) -> None:
        self._directory = Path(model_dir)
        self._lock = threading.Lock()
        self._inference_lock = threading.Lock()
        self._idempotency: OrderedDict[str, dict[str, object]] = OrderedDict()
        self._max_records = max_idempotency_records
        self._model = None
        self._pipeline = None
        self._voice_file: Path | None = None

    def _load(self) -> None:
        if self._pipeline is not None:
            return
        model_file = self._directory / "kokoro-v1_0.pth"
        config_file = self._directory / "config.json"
        voice_file = self._directory / "voices" / "af_heart.pt"
        _verify(model_file, MODEL_SHA256)
        _verify(config_file, CONFIG_SHA256)
        _verify(voice_file, VOICE_SHA256)
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
        self._voice_file = voice_file

    def ready(self) -> None:
        with self._lock:
            self._load()

    def synthesize(
        self,
        text: str,
        voice_id: str,
        locale: str,
        audio_format: str,
        idempotency_key: str | None,
    ) -> tuple[dict[str, object], bool]:
        if voice_id != "af_heart":
            raise VoiceUnknown(f"unknown voice {voice_id!r}")
        if audio_format == "mp3":
            raise ValueError("mp3 output is not supported by the local PCM worker")
        if not text.strip() or len(text) > 2000:
            raise ValueError("speech text must contain 1-2000 characters")
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
            with torch.inference_mode():
                assert self._pipeline is not None and self._voice_file is not None
                for result in self._pipeline(text, voice=str(self._voice_file), speed=0.95):
                    if result.audio is None:
                        continue
                    chunk = result.audio.detach().cpu().numpy()
                    if chunk.ndim != 1 or not np.isfinite(chunk).all():
                        raise ModelUnavailable("Kokoro produced an invalid waveform")
                    sample_count += chunk.size
                    if sample_count > NATIVE_SAMPLE_RATE_HZ * 120:
                        raise ValueError("speech duration exceeds 120 seconds")
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
