"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Literal

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
        model_id = os.environ.get("INFERENCE_MODEL_ID") or None
        return cls(
            worker_token=token if token else None,
            backend=backend,
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
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
