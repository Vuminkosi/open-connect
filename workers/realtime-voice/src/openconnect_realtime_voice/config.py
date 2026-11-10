"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass

from openconnect_realtime_voice.engine_mode import (
    EngineModeConfigError,
    fake_requested,
    is_production,
    parse_device,
    parse_offline,
    parse_provisioning,
    resolve_cache_dir,
    resolve_environment,
    validate_startup,
)

TRANSCRIPTION_MODE_ENV_VAR = "OC_TRANSCRIPTION_MODE"
DEFAULT_TRANSCRIPTION_MODE = "ENDPOINTING"
TRANSCRIPTION_MODES = ("ENDPOINTING", "PUSH_TO_TALK", "DIARIZATION")


def parse_transcription_mode(raw: str | None) -> str:
    """Normalize OC_TRANSCRIPTION_MODE; unknown values fail closed at startup.

    ``ENDPOINTING`` is the dialogue-style continuous turn-taking mode;
    ``DIARIZATION`` adds speaker attribution; ``PUSH_TO_TALK`` is manual.
    """
    if raw is None or raw.strip() == "":
        return DEFAULT_TRANSCRIPTION_MODE
    value = raw.strip().upper()
    if value not in TRANSCRIPTION_MODES:
        raise EngineModeConfigError(
            f"{TRANSCRIPTION_MODE_ENV_VAR} must be one of "
            f"{', '.join(TRANSCRIPTION_MODES)}, got {raw!r}"
        )
    return value


def _as_bool(value: str | None, default: bool) -> bool:
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    """Worker configuration; auth must be configured before serving requests."""

    worker_token: str | None
    orchestration_only: bool
    model_dir: str
    fake_engine: bool
    is_production: bool
    model_device: str
    provisioning: str = "first-start-if-missing"
    transcription_mode: str = DEFAULT_TRANSCRIPTION_MODE
    environment: str = "Production"
    model_offline: bool = False

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        # Canonical engine switch is OC_WORKER_ENGINE (real|fake, default
        # real); OPENCONNECT_FAKE_ENGINE is the deprecated alias (OC_ wins).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        return cls(
            worker_token=token if token else None,
            orchestration_only=_as_bool(os.environ.get("OC_REALTIME_ORCHESTRATION_ONLY"), True),
            # Canonical cache dir is OC_MODEL_CACHE_DIR (default /models);
            # MODEL_DIR is the deprecated alias (OC_ wins). Informational
            # until a provisioner lands: never triggers downloads.
            model_dir=resolve_cache_dir(
                os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR")
            ),
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
            transcription_mode=parse_transcription_mode(os.environ.get(TRANSCRIPTION_MODE_ENV_VAR)),
            environment=resolve_environment(),
            model_offline=parse_offline(),
        )

    @property
    def mode(self) -> str:
        return "orchestration-only" if self.orchestration_only else "local"
