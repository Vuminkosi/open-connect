"""Runtime configuration and verified voice registry for the local-only Kokoro worker."""

from __future__ import annotations

from dataclasses import dataclass
import os
import re

_VOICE_PATTERN = re.compile(r"^[a-z]{2}_[a-z0-9_]{1,32}$")

DEFAULT_VOICE_ID = "af_heart"
DEFAULT_LOCALE = "en-US"
DEFAULT_SPEED = 0.95
MIN_SPEED = 0.7
MAX_SPEED = 1.4

DEFAULT_MAX_PENDING_STREAMS = 4
DEFAULT_INFERENCE_WAIT_TIMEOUT_SECONDS = 5.0


@dataclass(frozen=True)
class VerifiedVoice:
    """Pinned, integrity-verified Kokoro voice asset definition."""

    voice_id: str
    locale: str
    artifact_relative_path: str
    artifact_url: str
    sha256: str
    default_speed: float = DEFAULT_SPEED
    min_speed: float = MIN_SPEED
    max_speed: float = MAX_SPEED


VERIFIED_VOICES: dict[str, VerifiedVoice] = {
    "af_heart": VerifiedVoice(
        voice_id="af_heart",
        locale="en-US",
        artifact_relative_path="voices/af_heart.pt",
        artifact_url="https://huggingface.co/hexgrad/Kokoro-82M/resolve/main/voices/af_heart.pt",
        sha256="0ab5709b8ffab19bfd849cd11d98f75b60af7733253ad0d67b12382a102cb4ff",
        default_speed=0.95,
        min_speed=0.7,
        max_speed=1.4,
    ),
}


def is_valid_voice_id(voice_id: str) -> bool:
    """Return True only for syntactically valid, verified registry voice IDs."""
    return isinstance(voice_id, str) and bool(_VOICE_PATTERN.fullmatch(voice_id)) and voice_id in VERIFIED_VOICES


def get_verified_voice(voice_id: str) -> VerifiedVoice | None:
    """Return the VerifiedVoice entry for voice_id, or None if unverified/invalid."""
    if not is_valid_voice_id(voice_id):
        return None
    return VERIFIED_VOICES[voice_id]


def _positive_int_env(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or not raw.strip():
        return default
    value = int(raw.strip())
    if value < 1:
        raise ValueError(f"{name} must be >= 1")
    return value


def _positive_float_env(name: str, default: float) -> float:
    raw = os.environ.get(name)
    if raw is None or not raw.strip():
        return default
    value = float(raw.strip())
    if value <= 0.0:
        raise ValueError(f"{name} must be > 0")
    return value


@dataclass(frozen=True)
class Settings:
    worker_token: str | None
    model_dir: str
    model_device: str
    voices: tuple[str, ...]
    default_voice: str
    kokoro_model: str = "hexgrad/Kokoro-82M"
    environment: str = "Production"
    max_pending_streams: int = DEFAULT_MAX_PENDING_STREAMS
    inference_wait_timeout_seconds: float = DEFAULT_INFERENCE_WAIT_TIMEOUT_SECONDS

    @classmethod
    def from_environment(cls) -> "Settings":
        raw_voices = os.environ.get("KOKORO_VOICES") or os.environ.get("OC_KOKORO_VOICE") or DEFAULT_VOICE_ID
        voices = tuple(v.strip() for v in raw_voices.split(",") if v.strip())
        if not voices or any(not is_valid_voice_id(v) for v in voices):
            supported = ", ".join(sorted(VERIFIED_VOICES))
            raise ValueError(f"The OpenConnect Kokoro worker supports only verified voices: {supported}")
        unique_voices = tuple(dict.fromkeys(voices))
        default_voice = DEFAULT_VOICE_ID if DEFAULT_VOICE_ID in unique_voices else unique_voices[0]
        return cls(
            worker_token=os.environ.get("OPENCONNECT_WORKER_TOKEN") or None,
            model_dir=(os.environ.get("OC_MODEL_CACHE_DIR") or os.environ.get("MODEL_DIR") or "/models").strip(),
            model_device="cpu",
            voices=unique_voices,
            default_voice=default_voice,
            kokoro_model=os.environ.get("OC_KOKORO_MODEL", "hexgrad/Kokoro-82M").strip(),
            environment=os.environ.get("OC_ENVIRONMENT", "Production").strip(),
            max_pending_streams=_positive_int_env("OC_KOKORO_MAX_PENDING_STREAMS", DEFAULT_MAX_PENDING_STREAMS),
            inference_wait_timeout_seconds=_positive_float_env(
                "OC_KOKORO_INFERENCE_WAIT_TIMEOUT_SECONDS", DEFAULT_INFERENCE_WAIT_TIMEOUT_SECONDS
            ),
        )
