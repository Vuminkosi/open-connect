"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from collections.abc import Mapping
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
    parse_provisioning,
    resolve_cache_dir,
    validate_startup,
    warn_deprecated_alias,
)

InferenceBackendKind = Literal["existing-local-endpoint", "python-transformers"]

_DEFAULT_BACKEND: InferenceBackendKind = "python-transformers"

DEFAULT_INFERENCE_PROVIDER = "meta-model-api"
DEFAULT_INFERENCE_MODEL = "muse-spark-1.3-contributor"
DEFAULT_INFERENCE_API_BASE_URL = "https://api.meta.ai/v1"

CONTRIBUTOR_MODEL_MARKER = "contributor"
CONTRIBUTOR_APPROVAL_ENV_VAR = "OC_CONTRIBUTOR_DATA_USE_APPROVED"
CONTRIBUTOR_CODE = "contributor_approval_required"


class ContributorApprovalError(ValueError):
    """Startup refusal: contributor-tier model without explicit approval."""


def contributor_approved(env: Mapping[str, str] | None = None) -> bool:
    """True only when OC_CONTRIBUTOR_DATA_USE_APPROVED is exactly ``1``."""
    raw = (
        env.get(CONTRIBUTOR_APPROVAL_ENV_VAR)
        if env is not None
        else os.environ.get(CONTRIBUTOR_APPROVAL_ENV_VAR)
    )
    return raw == "1"


def contributor_gate_blocks(model_id: str | None, env: Mapping[str, str] | None = None) -> bool:
    """Whether the contributor-tier approval gate blocks this model.

    Meta states that prompts, outputs and other submitted content in Muse
    Spark Contributor mode may be used to train future Meta models. For a
    privacy-first deployment that data-use tier requires explicit trusted
    operator approval (OC_CONTRIBUTOR_DATA_USE_APPROVED=1, exact); without it
    the worker fails closed at startup and on execute (HTTP 503
    ``contributor_approval_required``) instead of silently sending protected
    text under contributor terms. Use the standard ``muse-spark-1.3`` tier or
    another authorized provider when contributor terms are unacceptable; do
    not silently switch tiers.
    """
    return (
        model_id is not None
        and CONTRIBUTOR_MODEL_MARKER in model_id.lower()
        and not contributor_approved(env)
    )


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
    provisioning: str = "first-start-if-missing"
    inference_provider: str = DEFAULT_INFERENCE_PROVIDER

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
        # Canonical engine switch is OC_WORKER_ENGINE (real|fake, default
        # real); OPENCONNECT_FAKE_ENGINE is the deprecated alias (OC_ wins).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        # Canonical cloud endpoint is OC_INFERENCE_API_BASE_URL;
        # INFERENCE_CLOUD_URL is the deprecated alias (OC_ wins). Either set
        # means the cloud leg is configured. A configured URL without a key
        # from a secret mount fails closed at startup.
        cloud_url_raw = _resolve_with_default(
            os.environ.get("OC_INFERENCE_API_BASE_URL"),
            os.environ.get("INFERENCE_CLOUD_URL"),
            "INFERENCE_CLOUD_URL",
            "OC_INFERENCE_API_BASE_URL",
            DEFAULT_INFERENCE_API_BASE_URL,
        )
        cloud_url = validate_cloud_url(cloud_url_raw)
        cloud_key = os.environ.get("INFERENCE_CLOUD_API_KEY") or None
        if cloud_url is not None and not cloud_key:
            raise CloudConfigError(
                "cloud inference URL is set but INFERENCE_CLOUD_API_KEY is "
                "missing; refusing to start without a secret-mounted key"
            )
        # Contributor tier requires explicit operator approval of the
        # contributor data-use terms (see contributor_gate_blocks).
        model_id = _resolve_alias(
            os.environ.get("OC_INFERENCE_MODEL"),
            os.environ.get("INFERENCE_MODEL_ID"),
            "INFERENCE_MODEL_ID",
            "OC_INFERENCE_MODEL",
        )
        if model_id is None:
            model_id = DEFAULT_INFERENCE_MODEL
        if contributor_gate_blocks(model_id):
            raise ContributorApprovalError(
                f"inference model {model_id!r} is a contributor data-use tier; "
                f"set {CONTRIBUTOR_APPROVAL_ENV_VAR}=1 to approve, or use a "
                "non-contributor model/provider"
            )
        try:
            cloud_timeout = int(os.environ.get("INFERENCE_CLOUD_TIMEOUT_MS", "5000"))
        except ValueError:
            cloud_timeout = 5000
        provider_raw = os.environ.get("OC_INFERENCE_PROVIDER")
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
            # Canonical cache dir is OC_MODEL_CACHE_DIR (default /models);
            # MODEL_DIR is the deprecated alias (OC_ wins). Informational
            # until a provisioner lands: never triggers downloads.
            model_dir=resolve_cache_dir(
                os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR")
            ),
            endpoint_url=os.environ.get("INFERENCE_ENDPOINT_URL") or None,
            enable_tools=_as_bool(os.environ.get("INFERENCE_ENABLE_TOOLS")),
            enable_structured_output=_as_bool(os.environ.get("INFERENCE_ENABLE_STRUCTURED_OUTPUT")),
            enable_embeddings=_as_bool(os.environ.get("INFERENCE_ENABLE_EMBEDDINGS")),
            max_messages=_as_int(os.environ.get("INFERENCE_MAX_MESSAGES"), 64),
            max_input_chars=_as_int(os.environ.get("INFERENCE_MAX_INPUT_CHARS"), 32_768),
            max_output_tokens=_as_int(os.environ.get("INFERENCE_MAX_OUTPUT_TOKENS"), 1_024),
            max_tasks=_as_int(os.environ.get("INFERENCE_MAX_TASKS"), 1_000),
            default_lease_ms=_as_int(os.environ.get("INFERENCE_DEFAULT_LEASE_MS"), 30_000),
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
            inference_provider=provider_raw.strip()
            if provider_raw is not None and provider_raw.strip() != ""
            else DEFAULT_INFERENCE_PROVIDER,
        )
