"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Literal

from openconnect_transcription.cloud import (
    CloudConfigError,
    resolve_route_order,
    validate_cloud_url,
)
from openconnect_transcription.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    validate_startup,
)

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
    fake_engine: bool
    is_production: bool
    model_device: str
    cloud_url: str | None
    cloud_api_key: str | None
    route_order: str
    cloud_timeout_ms: int
    privacy_zone: str
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
        # Fake is env-gated on the exact OPENCONNECT_FAKE_ENGINE name with the
        # strict 1/true/yes set ("on" does NOT enable fake mode).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        # Cloud is opt-in only: unset/empty URL means local-only. A set URL is
        # allowlisted at startup and requires a key from a secret mount.
        cloud_url = validate_cloud_url(os.environ.get("TRANSCRIPTION_CLOUD_URL"))
        cloud_key = os.environ.get("TRANSCRIPTION_CLOUD_API_KEY") or None
        if cloud_url is not None and not cloud_key:
            raise CloudConfigError(
                "TRANSCRIPTION_CLOUD_URL is set but TRANSCRIPTION_CLOUD_API_KEY "
                "is missing; refusing to start without a secret-mounted key"
            )
        try:
            cloud_timeout = int(os.environ.get("TRANSCRIPTION_CLOUD_TIMEOUT_MS", "5000"))
        except ValueError:
            cloud_timeout = 5000
        return cls(
            worker_token=token if token else None,
            backend=backend,
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            cloud_url=cloud_url,
            cloud_api_key=cloud_key,
            route_order=resolve_route_order(
                os.environ.get("TRANSCRIPTION_ROUTE_ORDER"),
                cloud_configured=cloud_url is not None,
            ),
            cloud_timeout_ms=cloud_timeout if cloud_timeout > 0 else 5000,
            privacy_zone=os.environ.get("OC_PRIVACY_ZONE", ""),
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
