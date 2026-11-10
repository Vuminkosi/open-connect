"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass


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

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        return cls(
            worker_token=token if token else None,
            orchestration_only=_as_bool(os.environ.get("OC_REALTIME_ORCHESTRATION_ONLY"), True),
            model_dir=os.environ.get("MODEL_DIR", "/models"),
        )

    @property
    def mode(self) -> str:
        return "orchestration-only" if self.orchestration_only else "local"
