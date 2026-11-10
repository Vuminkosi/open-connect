"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass

from openconnect_realtime_voice.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    validate_startup,
)


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

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        # Fake is env-gated on the exact OPENCONNECT_FAKE_ENGINE name with the
        # strict 1/true/yes set ("on" does NOT enable fake mode).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        return cls(
            worker_token=token if token else None,
            orchestration_only=_as_bool(os.environ.get("OC_REALTIME_ORCHESTRATION_ONLY"), True),
            model_dir=os.environ.get("MODEL_DIR", "/models"),
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
        )

    @property
    def mode(self) -> str:
        return "orchestration-only" if self.orchestration_only else "local"
