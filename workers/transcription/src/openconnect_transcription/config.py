"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Literal

TranscriptionBackendKind = Literal["local-fake", "local-model", "remote-endpoint"]
PrivacyModeKind = Literal["strict-local", "local-only", "policy-controlled"]

_DEFAULT_BACKEND: TranscriptionBackendKind = "local-fake"


def _as_int(value: str | None, default: int) -> int:
    try:
        return int(value) if value is not None and value.strip() != "" else default
    except ValueError:
        return default


@dataclass(frozen=True)
class Settings:
    """Worker configuration.

    ``strict-local`` privacy mode blocks any remote STT backend: unmasked
    audio must never leave the operator-controlled trusted boundary.
    """

    worker_token: str | None
    backend: TranscriptionBackendKind
    privacy_mode: PrivacyModeKind
    model_id: str | None
    model_revision: str | None
    model_dir: str
    endpoint_url: str | None
    max_audio_bytes: int
    max_duration_ms: int
    max_tasks: int
    default_lease_ms: int
    default_locale: str

    @classmethod
    def from_environment(cls) -> "Settings":
        backend_raw = os.environ.get("TRANSCRIPTION_BACKEND", _DEFAULT_BACKEND).strip()
        backend: TranscriptionBackendKind = (
            backend_raw
            if backend_raw in ("local-fake", "local-model", "remote-endpoint")
            else _DEFAULT_BACKEND
        )
        privacy_raw = os.environ.get("TRANSCRIPTION_PRIVACY_MODE", "strict-local").strip()
        privacy_mode: PrivacyModeKind = (
            privacy_raw
            if privacy_raw in ("strict-local", "local-only", "policy-controlled")
            else "strict-local"
        )
        token = os.environ.get("OPENCONNECT_TRANSCRIPTION_TOKEN") or os.environ.get(
            "OPENCONNECT_WORKER_TOKEN"
        )
        return cls(
            worker_token=token if token else None,
            backend=backend,
            privacy_mode=privacy_mode,
            model_id=os.environ.get("TRANSCRIPTION_MODEL_ID") or None,
            model_revision=os.environ.get("TRANSCRIPTION_MODEL_REVISION") or None,
            model_dir=os.environ.get("MODEL_DIR", "/models"),
            endpoint_url=os.environ.get("TRANSCRIPTION_ENDPOINT_URL") or None,
            max_audio_bytes=_as_int(os.environ.get("TRANSCRIPTION_MAX_AUDIO_BYTES"), 5_242_880),
            max_duration_ms=_as_int(os.environ.get("TRANSCRIPTION_MAX_DURATION_MS"), 60_000),
            max_tasks=_as_int(os.environ.get("TRANSCRIPTION_MAX_TASKS"), 1_000),
            default_lease_ms=_as_int(os.environ.get("TRANSCRIPTION_DEFAULT_LEASE_MS"), 30_000),
            default_locale=os.environ.get("TRANSCRIPTION_DEFAULT_LOCALE", "en"),
        )

    @property
    def remote_blocked(self) -> bool:
        """Remote STT is blocked unless policy explicitly permits egress."""
        return self.backend == "remote-endpoint" and self.privacy_mode == "strict-local"
