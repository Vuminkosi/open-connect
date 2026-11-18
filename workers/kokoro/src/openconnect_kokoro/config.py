"""Runtime configuration for the local-only Kokoro worker."""

from __future__ import annotations

from dataclasses import dataclass
import os
import re

_VOICE_PATTERN = re.compile(r"^[a-z]{2}_[a-z0-9_]{1,32}$")


@dataclass(frozen=True)
class Settings:
    worker_token: str | None
    model_dir: str
    model_device: str
    voices: tuple[str, ...]
    default_voice: str
    kokoro_model: str = "hexgrad/Kokoro-82M"
    environment: str = "Production"

    @classmethod
    def from_environment(cls) -> "Settings":
        raw_voices = os.environ.get("KOKORO_VOICES") or os.environ.get("OC_KOKORO_VOICE") or "af_heart"
        voices = tuple(v.strip() for v in raw_voices.split(",") if v.strip())
        verified = tuple(v for v in voices if _VOICE_PATTERN.fullmatch(v))
        if verified != ("af_heart",):
            raise ValueError("The OpenConnect Kokoro worker supports only af_heart")
        return cls(
            worker_token=os.environ.get("OPENCONNECT_WORKER_TOKEN") or None,
            model_dir=(os.environ.get("OC_MODEL_CACHE_DIR") or os.environ.get("MODEL_DIR") or "/models").strip(),
            model_device="cpu",
            voices=("af_heart",),
            default_voice="af_heart",
            kokoro_model=os.environ.get("OC_KOKORO_MODEL", "hexgrad/Kokoro-82M").strip(),
            environment=os.environ.get("OC_ENVIRONMENT", "Production").strip(),
        )
