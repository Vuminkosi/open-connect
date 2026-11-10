"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Literal

from openconnect_inference.cloud import (
    CloudConfigError,
    resolve_route_order,
    validate_cloud_url,
)
from openconnect_inference.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    validate_startup,
)

InferenceBackendKind = Literal["existing-local-endpoint", "python-transformers"]

_DEFAULT_BACKEND: InferenceBackendKind = "python-transformers"


def _as_bool(value: str | None, default: bool = False) -> bool:
    if value is None:
        return default
    return value.strip().lower() in ("1", "true", "yes", "on")


def _as_int(value: str | None, default: int) -> int:
    try:
        return int(value) if value is not None and value.strip() != "" else default
    except ValueError:
        return default


@dataclass(frozen=True)
class Settings:
    """Worker configuration.

    Adapter mode (``existing-local-endpoint``) delegates to a host-provided
    local endpoint and MUST NOT download models. The bundled fake engine
    performs no network calls and no downloads in either mode.
    """

    worker_token: str | None
    backend: InferenceBackendKind
    fake_engine: bool
    is_production: bool
    model_device: str
    cloud_url: str | None
    cloud_api_key: str | None
    route_order: str
    cloud_timeout_ms: int
    privacy_zone: str
    model_id: str | None
    model_revision: str | None
    model_dir: str
    endpoint_url: str | None
    enable_tools: bool
    enable_structured_output: bool
    enable_embeddings: bool
    max_messages: int
    max_input_chars: int
    max_output_tokens: int
    max_tasks: int
    default_lease_ms: int

    @classmethod
    def from_environment(cls) -> "Settings":
        backend_raw = os.environ.get("INFERENCE_BACKEND", _DEFAULT_BACKEND).strip()
        backend: InferenceBackendKind = (
            backend_raw
            if backend_raw in ("existing-local-endpoint", "python-transformers")
            else _DEFAULT_BACKEND
        )
        token = os.environ.get("OPENCONNECT_INFERENCE_TOKEN") or os.environ.get(
            "OPENCONNECT_WORKER_TOKEN"
        )
        # Fake is env-gated on the exact OPENCONNECT_FAKE_ENGINE name with the
        # strict 1/true/yes set ("on" does NOT enable fake mode).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        # Cloud is opt-in only: unset/empty URL means local-only. A set URL is
        # allowlisted at startup and requires a key from a secret mount.
        cloud_url = validate_cloud_url(os.environ.get("INFERENCE_CLOUD_URL"))
        cloud_key = os.environ.get("INFERENCE_CLOUD_API_KEY") or None
        if cloud_url is not None and not cloud_key:
            raise CloudConfigError(
                "INFERENCE_CLOUD_URL is set but INFERENCE_CLOUD_API_KEY is missing; "
                "refusing to start without a secret-mounted key"
            )
        try:
            cloud_timeout = int(os.environ.get("INFERENCE_CLOUD_TIMEOUT_MS", "5000"))
        except ValueError:
            cloud_timeout = 5000
        model_id = os.environ.get("INFERENCE_MODEL_ID") or None
        return cls(
            worker_token=token if token else None,
            backend=backend,
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            cloud_url=cloud_url,
            cloud_api_key=cloud_key,
            route_order=resolve_route_order(
                os.environ.get("INFERENCE_ROUTE_ORDER"),
                cloud_configured=cloud_url is not None,
            ),
            cloud_timeout_ms=cloud_timeout if cloud_timeout > 0 else 5000,
            privacy_zone=os.environ.get("OC_PRIVACY_ZONE", ""),
            model_id=model_id,
            model_revision=os.environ.get("INFERENCE_MODEL_REVISION") or None,
            model_dir=os.environ.get("MODEL_DIR", "/models"),
            endpoint_url=os.environ.get("INFERENCE_ENDPOINT_URL") or None,
            enable_tools=_as_bool(os.environ.get("INFERENCE_ENABLE_TOOLS")),
            enable_structured_output=_as_bool(os.environ.get("INFERENCE_ENABLE_STRUCTURED_OUTPUT")),
            enable_embeddings=_as_bool(os.environ.get("INFERENCE_ENABLE_EMBEDDINGS")),
            max_messages=_as_int(os.environ.get("INFERENCE_MAX_MESSAGES"), 64),
            max_input_chars=_as_int(os.environ.get("INFERENCE_MAX_INPUT_CHARS"), 32_768),
            max_output_tokens=_as_int(os.environ.get("INFERENCE_MAX_OUTPUT_TOKENS"), 1_024),
            max_tasks=_as_int(os.environ.get("INFERENCE_MAX_TASKS"), 1_000),
            default_lease_ms=_as_int(os.environ.get("INFERENCE_DEFAULT_LEASE_MS"), 30_000),
        )
