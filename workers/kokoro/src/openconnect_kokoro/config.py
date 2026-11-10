"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass

from openconnect_kokoro.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    validate_startup,
)

_VOICE_PATTERN = re.compile(r"^[a-z]{2}_[a-z0-9_]{1,32}$")


def _as_bool(value: str | None, default: bool) -> bool:
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    """Worker configuration; auth must be configured before serving requests."""

    worker_token: str | None
    model_dir: str
    fake_engine: bool
    is_production: bool
    model_device: str
    require_model: bool
    voices: tuple[str, ...]
    default_voice: str

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        # Fake is env-gated on the exact OPENCONNECT_FAKE_ENGINE name with the
        # strict 1/true/yes set ("on" does NOT enable fake mode).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        raw_voices = os.environ.get("KOKORO_VOICES", "af_heart")
        voices = tuple(voice.strip() for voice in raw_voices.split(",") if voice.strip()) or (
            "af_heart",
        )
        verified = tuple(voice for voice in voices if _VOICE_PATTERN.match(voice))
        default = os.environ.get("KOKORO_DEFAULT_VOICE", "af_heart")
        # af_heart is used as the default only when it verifies against the
        # configured voice inventory; otherwise fall back to the first verified voice.
        if default not in verified:
            default = verified[0] if verified else "af_heart"
        return cls(
            worker_token=token if token else None,
            model_dir=os.environ.get("MODEL_DIR", "/models"),
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            require_model=_as_bool(os.environ.get("KOKORO_REQUIRE_MODEL"), False),
            voices=verified,
            default_voice=default,
        )

    def model_marker_present(self) -> bool:
        marker = os.path.join(self.model_dir, "kokoro-init.json")
        return os.path.exists(marker)
