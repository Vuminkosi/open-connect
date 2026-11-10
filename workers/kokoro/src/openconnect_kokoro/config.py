"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
import re
from dataclasses import dataclass

from openconnect_kokoro.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    parse_provisioning,
    resolve_cache_dir,
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
    provisioning: str = "first-start-if-missing"

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        # Canonical engine switch is OC_WORKER_ENGINE (real|fake, default
        # real); OPENCONNECT_FAKE_ENGINE is the deprecated alias (OC_ wins).
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
            # Canonical cache dir is OC_MODEL_CACHE_DIR (default /models);
            # MODEL_DIR is the deprecated alias (OC_ wins). Informational
            # until a provisioner lands: never triggers downloads.
            model_dir=resolve_cache_dir(
                os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR")
            ),
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            require_model=_as_bool(os.environ.get("KOKORO_REQUIRE_MODEL"), False),
            voices=verified,
            default_voice=default,
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
        )

    def model_marker_present(self) -> bool:
        marker = os.path.join(self.model_dir, "kokoro-init.json")
        return os.path.exists(marker)
