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
    EngineModeConfigError,
    fake_requested,
    is_production,
    parse_device,
    parse_offline,
    parse_provisioning,
    resolve_cache_dir,
    resolve_environment,
    validate_startup,
    warn_deprecated_alias,
)

TranscriptionBackendKind = Literal["local-fake", "local-model", "remote-endpoint"]
PrivacyModeKind = Literal["strict-local", "local-only", "policy-controlled"]

_DEFAULT_BACKEND: TranscriptionBackendKind = "local-fake"

DEFAULT_TRANSCRIPTION_PROVIDER = "meta-model-api"
DEFAULT_TRANSCRIPTION_MODE = "ENDPOINTING"
TRANSCRIPTION_MODES = ("ENDPOINTING", "PUSH_TO_TALK", "DIARIZATION")
DEFAULT_TRANSCRIPTION_REALTIME_URL = "wss://api.meta.ai/v1/asr/realtime"
DEFAULT_TRANSCRIPTION_FILE_URL = "https://api.meta.ai/v1/asr/transcribe"


def parse_transcription_mode(raw: str | None) -> str:
    """Normalize OC_TRANSCRIPTION_MODE; unknown values fail closed at startup.

    ``ENDPOINTING`` is the dialogue-style continuous turn-taking mode (the
    provider wire mode for conversational speech detection and per-utterance
    finalization); ``DIARIZATION`` adds speaker attribution; ``PUSH_TO_TALK``
    is manual.
    """
    if raw is None or raw.strip() == "":
        return DEFAULT_TRANSCRIPTION_MODE
    value = raw.strip().upper()
    if value not in TRANSCRIPTION_MODES:
        raise EngineModeConfigError(
            f"OC_TRANSCRIPTION_MODE must be one of {', '.join(TRANSCRIPTION_MODES)}, got {raw!r}"
        )
    return value


def _resolve_alias(
    primary_raw: str | None, legacy_raw: str | None, legacy_name: str, primary_name: str
) -> str | None:
    """OC_* primary wins; legacy name still works with a one-time warning."""
    if primary_raw is not None and primary_raw.strip() != "":
        return primary_raw.strip()
    if legacy_raw is not None and legacy_raw.strip() != "":
        warn_deprecated_alias(legacy_name, primary_name)
        return legacy_raw.strip()
    return None


def _resolve_with_default(
    primary_raw: str | None,
    legacy_raw: str | None,
    legacy_name: str,
    primary_name: str,
    default: str,
) -> str | None:
    """Alias resolution with a documented default and an explicit opt-out.

    An unset primary falls back to the legacy alias, then to the documented
    default. An explicitly emptied primary (``""``) with no legacy value opts
    out entirely (returns ``None``): the only way to run local-only now that
    the cloud endpoint has a default.
    """
    if primary_raw is not None and primary_raw.strip() == "":
        if legacy_raw is None or legacy_raw.strip() == "":
            return None
    resolved = _resolve_alias(primary_raw, legacy_raw, legacy_name, primary_name)
    return resolved if resolved is not None else default


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
    provisioning: str = "first-start-if-missing"
    transcription_provider: str = DEFAULT_TRANSCRIPTION_PROVIDER
    transcription_mode: str = DEFAULT_TRANSCRIPTION_MODE
    transcription_realtime_url: str = DEFAULT_TRANSCRIPTION_REALTIME_URL
    transcription_file_url: str = DEFAULT_TRANSCRIPTION_FILE_URL
    environment: str = "Production"
    model_offline: bool = False

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
        # Canonical engine switch is OC_WORKER_ENGINE (real|fake, default
        # real); OPENCONNECT_FAKE_ENGINE is the deprecated alias (OC_ wins).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        # Canonical cloud file endpoint is OC_TRANSCRIPTION_FILE_URL;
        # TRANSCRIPTION_CLOUD_URL is the deprecated alias (OC_ wins). Either
        # set means the cloud leg is configured. A configured URL without a
        # key from a secret mount fails closed at startup. The cloud leg
        # additionally requires OC_TRANSCRIPTION_MODEL: without an exact
        # provider model ID (never guessed) the cloud path stays unready.
        cloud_url_raw = _resolve_with_default(
            os.environ.get("OC_TRANSCRIPTION_FILE_URL"),
            os.environ.get("TRANSCRIPTION_CLOUD_URL"),
            "TRANSCRIPTION_CLOUD_URL",
            "OC_TRANSCRIPTION_FILE_URL",
            DEFAULT_TRANSCRIPTION_FILE_URL,
        )
        cloud_url = validate_cloud_url(cloud_url_raw)
        cloud_key = os.environ.get("TRANSCRIPTION_CLOUD_API_KEY") or None
        if cloud_url is not None and not cloud_key:
            raise CloudConfigError(
                "cloud transcription URL is set but TRANSCRIPTION_CLOUD_API_KEY "
                "is missing; refusing to start without a secret-mounted key"
            )
        model_id = _resolve_alias(
            os.environ.get("OC_TRANSCRIPTION_MODEL"),
            os.environ.get("TRANSCRIPTION_MODEL_ID"),
            "TRANSCRIPTION_MODEL_ID",
            "OC_TRANSCRIPTION_MODEL",
        )
        try:
            cloud_timeout = int(os.environ.get("TRANSCRIPTION_CLOUD_TIMEOUT_MS", "5000"))
        except ValueError:
            cloud_timeout = 5000
        provider_raw = os.environ.get("OC_TRANSCRIPTION_PROVIDER")
        realtime_raw = os.environ.get("OC_TRANSCRIPTION_REALTIME_URL")
        file_raw = os.environ.get("OC_TRANSCRIPTION_FILE_URL")
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
            model_id=model_id,
            model_revision=os.environ.get("TRANSCRIPTION_MODEL_REVISION") or None,
            # Canonical cache dir is OC_MODEL_CACHE_DIR (default /models);
            # MODEL_DIR is the deprecated alias (OC_ wins). Informational
            # until a provisioner lands: never triggers downloads.
            model_dir=resolve_cache_dir(
                os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR")
            ),
            endpoint_url=os.environ.get("TRANSCRIPTION_ENDPOINT_URL") or None,
            max_audio_bytes=_as_int(os.environ.get("TRANSCRIPTION_MAX_AUDIO_BYTES"), 5_242_880),
            max_duration_ms=_as_int(os.environ.get("TRANSCRIPTION_MAX_DURATION_MS"), 60_000),
            max_tasks=_as_int(os.environ.get("TRANSCRIPTION_MAX_TASKS"), 1_000),
            default_lease_ms=_as_int(os.environ.get("TRANSCRIPTION_DEFAULT_LEASE_MS"), 30_000),
            default_locale=os.environ.get("TRANSCRIPTION_DEFAULT_LOCALE", "en"),
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
            transcription_provider=provider_raw.strip()
            if provider_raw is not None and provider_raw.strip() != ""
            else DEFAULT_TRANSCRIPTION_PROVIDER,
            transcription_mode=parse_transcription_mode(os.environ.get("OC_TRANSCRIPTION_MODE")),
            transcription_realtime_url=realtime_raw.strip()
            if realtime_raw is not None and realtime_raw.strip() != ""
            else DEFAULT_TRANSCRIPTION_REALTIME_URL,
            transcription_file_url=file_raw.strip()
            if file_raw is not None and file_raw.strip() != ""
            else DEFAULT_TRANSCRIPTION_FILE_URL,
            environment=resolve_environment(),
            model_offline=parse_offline(),
        )

    @property
    def remote_blocked(self) -> bool:
        """Remote STT is blocked unless policy explicitly permits egress."""
        return self.backend == "remote-endpoint" and self.privacy_mode == "strict-local"
