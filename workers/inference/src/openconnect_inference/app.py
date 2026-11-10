"""Private HTTP API for the self-hosted inference worker."""

from __future__ import annotations

import hashlib
import hmac
import logging
import time
import uuid
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Header, HTTPException, status

from openconnect_inference.config import Settings
from openconnect_inference import cloud as cloud_module
from openconnect_inference import engine_mode
from openconnect_inference.engine import FakeInferenceEngine, advertised_capabilities
from openconnect_inference.schemas import (
    CapabilityResponse,
    ExecuteRequest,
    ExecuteResponse,
    StreamChunk,
    TaskClaimRequest,
    TaskClaimResponse,
    TaskCompleteRequest,
    TaskFailRequest,
    TaskHeartbeatRequest,
    TaskHeartbeatResponse,
    TaskTerminalResponse,
    Usage,
)

logger = logging.getLogger("openconnect_inference")
app = FastAPI(title="OpenConnect Inference Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
engine = FakeInferenceEngine()

WORKER_VERSION = "0.1.0"

# In-memory broker state. Bounded; entries hold metadata only.
_tasks: dict[str, dict[str, Any]] = {}
_idempotency: dict[str, dict[str, Any]] = {}


def _now_ms() -> int:
    return int(time.time() * 1000)


def _settings() -> Settings:
    return settings


def authenticate(authorization: Annotated[str | None, Header()] = None) -> None:
    token = _settings().worker_token
    if token is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="worker unavailable"
        )
    supplied = authorization.removeprefix("Bearer ") if authorization else ""
    if (
        not authorization
        or not authorization.startswith("Bearer ")
        or not hmac.compare_digest(supplied, token)
    ):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="unauthorized")


def _capabilities(cfg: Settings) -> list[str]:
    return advertised_capabilities(
        model_id=cfg.model_id,
        enable_tools=cfg.enable_tools,
        enable_structured_output=cfg.enable_structured_output,
        enable_embeddings=cfg.enable_embeddings,
    )


def _get_task(task_id: str) -> dict[str, Any]:
    try:
        return _tasks[task_id]
    except KeyError:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND, detail="task_not_found"
        ) from None


def _require_live_lease(entry: dict[str, Any], lease_id: str) -> None:
    if entry["state"] in ("completed", "failed"):
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="conflict")
    if not hmac.compare_digest(str(entry["lease_id"]), lease_id):
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="conflict")
    if entry["lease_expires_at_ms"] <= _now_ms():
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="lease_expired")


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    cfg = _settings()
    if cfg.backend == "existing-local-endpoint" and not cfg.endpoint_url:
        # Fail closed: adapter mode without a configured endpoint is not ready.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="endpoint_unavailable",
        )
    info, status_code, code = engine_mode.rejection(cfg.fake_engine, cfg.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    assert info is not None
    if cfg.fake_engine:
        return {
            "status": "ready",
            "backend": cfg.backend,
            "modelLoaded": cfg.model_id is not None,
            "capabilities": _capabilities(cfg),
            "device": info.effective,
            "deviceRequested": info.requested,
            **_routing_status(cfg),
        }
    # Real mode: this build ships no local LLM backend, so readiness depends
    # on the opt-in cloud leg. Cloud + local both down -> typed unready.
    if not _real_legs_available(cfg):
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="model_unavailable",
        )
    return {
        "status": "ready",
        "backend": cfg.backend,
        "modelLoaded": cfg.model_id is not None,
        "capabilities": _capabilities(cfg),
        "device": info.effective,
        "deviceRequested": info.requested,
        **_routing_status(cfg),
    }


class _LocalLegUnavailable(RuntimeError):
    """Local leg cannot serve (no local engine provisioned in this build)."""


def _routing_status(cfg: Settings) -> dict[str, object]:
    return {
        "routeOrder": cfg.route_order,
        "cloudConfigured": cfg.cloud_url is not None,
        "cloudAvailable": _cloud_leg_available(cfg),
        "localAvailable": False,  # reserved: no local engine in this build
    }


def _cloud_leg_available(cfg: Settings) -> bool:
    return (
        cfg.cloud_url is not None
        and cfg.cloud_api_key is not None
        and not cloud_module.privacy_zone_strict()
    )


def _real_legs_available(cfg: Settings) -> bool:
    return _cloud_leg_available(cfg)


def _try_local_real(cfg: Settings) -> str:
    """Local leg: raises because this build provisions no local engine.

    Kept as a separate function so the routing chain (local-first order,
    cloud-down fallback) is observable and unit-testable, and so a future
    provisioned local backend can serve here without touching the router.
    """
    raise _LocalLegUnavailable("local engine not provisioned in this build")


def _try_cloud_real(cfg: Settings, messages: list, max_tokens: int | None, stream: bool) -> str:
    """Cloud leg. Raises CloudPolicyDenied (terminal) or CloudLegFailed/leg errors."""
    assert cfg.cloud_url is not None and cfg.cloud_api_key is not None
    if cloud_module.privacy_zone_strict():
        # Terminal: strict-local disables the cloud path entirely.
        raise cloud_module.CloudPolicyDenied("privacy_blocked")
    if stream:
        # The cloud leg serves content, not streaming framing.
        raise cloud_module.CloudLegFailed("cloud leg does not serve streaming")
    payload = {
        "messages": [{"role": m.role, "content": m.content} for m in messages],
        "maxTokens": max_tokens,
    }
    response = cloud_module.post_json(
        url=cfg.cloud_url,
        payload=payload,
        api_key=cfg.cloud_api_key,
        timeout_s=cfg.cloud_timeout_ms / 1000.0,
    )
    output = response.get("output")
    if not isinstance(output, str) or not output:
        raise cloud_module.CloudLegFailed("cloud leg returned no output")
    return output


def _ordered_legs(cfg: Settings) -> list[str]:
    if cfg.cloud_url is None:
        return ["local"]
    if cfg.route_order == cloud_module.LOCAL_FIRST:
        return ["local", "cloud"]
    return ["cloud", "local"]


def _execute_real_routed(
    cfg: Settings, messages: list, max_tokens: int | None, stream: bool
) -> str:
    """Route across cloud/local legs. Exhaustion -> 503 model_unavailable."""
    for leg in _ordered_legs(cfg):
        try:
            if leg == "cloud":
                if cfg.cloud_url is None:
                    continue
                return _try_cloud_real(cfg, messages, max_tokens, stream)
            return _try_local_real(cfg)
        except cloud_module.CloudPolicyDenied as exc:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=exc.code
            ) from exc
        except (cloud_module.CloudLegFailed, _LocalLegUnavailable) as exc:
            logger.info("Real leg %s failed; trying next leg outcome=%s", leg, type(exc).__name__)
            continue
    raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable")


@app.get(
    "/internal/v1/capabilities",
    response_model=CapabilityResponse,
    dependencies=[Depends(authenticate)],
)
def capabilities() -> CapabilityResponse:
    cfg = _settings()
    return CapabilityResponse(
        worker="inference",
        version=WORKER_VERSION,
        backend=cfg.backend,
        engine="fake-bounded" if cfg.fake_engine else "real",
        modelId=cfg.model_id,
        modelRevision=cfg.model_revision,
        modelLoaded=cfg.model_id is not None,
        capabilities=_capabilities(cfg),
        privacyMode="strict-local",
    )


@app.post(
    "/internal/v1/execute",
    response_model=ExecuteResponse,
    dependencies=[Depends(authenticate)],
)
def execute(request: ExecuteRequest) -> ExecuteResponse:
    cfg = _settings()
    if cfg.backend == "existing-local-endpoint" and not cfg.endpoint_url:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="endpoint_unavailable",
        )
    _, status_code, code = engine_mode.rejection(cfg.fake_engine, cfg.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    if not cfg.fake_engine:
        return _execute_real_request(cfg, request)
    advertised = _capabilities(cfg)
    unsupported = [c for c in request.input.requestedCapabilities if c not in advertised]
    if unsupported:
        # Truthful discovery: never silently serve an unadvertised capability.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="capability_unavailable",
        )
    total_input_chars = sum(len(m.content) for m in request.input.messages)
    if total_input_chars > cfg.max_input_chars:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="invalid_request")

    idem_key = str(request.idempotencyKey) if request.idempotencyKey else None
    if idem_key and idem_key in _idempotency:
        cached = _idempotency[idem_key]
        return ExecuteResponse(**cached)

    output, input_chars = engine.complete(request.input.messages, request.input.maxTokens)
    chunks: list[StreamChunk] | None = None
    capabilities_used = ["chat"]
    if request.input.stream:
        if "stream" not in advertised:  # pragma: no cover - defensive
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="capability_unavailable",
            )
        chunks = engine.stream(request.input.messages, request.input.maxTokens)
        capabilities_used.append("stream")
    for extra in ("tools", "structured-output", "embeddings"):
        if extra in request.input.requestedCapabilities:
            capabilities_used.append(extra)

    # Log metadata only: never message content.
    logger.info(
        "Inference execute request_id=%s backend=%s messages=%d input_chars=%d stream=%s",
        request.requestId,
        cfg.backend,
        len(request.input.messages),
        input_chars,
        request.input.stream,
    )
    response = ExecuteResponse(
        schemaVersion="openconnect.v1",
        requestId=request.requestId,
        backend=cfg.backend,
        modelId=cfg.model_id,
        output=output,
        usage=Usage(inputChars=input_chars, outputChars=len(output)),
        capabilitiesUsed=capabilities_used,
        chunks=chunks,
    )
    if idem_key:
        if len(_idempotency) >= 500:
            _idempotency.pop(next(iter(_idempotency)))
        _idempotency[idem_key] = response.model_dump(mode="json")
    return response


def _execute_real_request(cfg: Settings, request: ExecuteRequest) -> ExecuteResponse:
    """Real-mode execute: policy checks first, then cloud/local routing."""
    advertised = _capabilities(cfg)
    unsupported = [c for c in request.input.requestedCapabilities if c not in advertised]
    if unsupported:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="capability_unavailable",
        )
    total_input_chars = sum(len(m.content) for m in request.input.messages)
    if total_input_chars > cfg.max_input_chars:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="invalid_request")

    idem_key = str(request.idempotencyKey) if request.idempotencyKey else None
    if idem_key and idem_key in _idempotency:
        cached = _idempotency[idem_key]
        return ExecuteResponse(**cached)

    output = _execute_real_routed(
        cfg, request.input.messages, request.input.maxTokens, request.input.stream
    )
    # Log metadata only: never message content, never cloud secrets.
    logger.info(
        "Inference execute request_id=%s backend=%s messages=%d input_chars=%d stream=%s",
        request.requestId,
        cfg.backend,
        len(request.input.messages),
        total_input_chars,
        request.input.stream,
    )
    response = ExecuteResponse(
        schemaVersion="openconnect.v1",
        requestId=request.requestId,
        backend=cfg.backend,
        modelId=cfg.model_id,
        output=output,
        usage=Usage(inputChars=total_input_chars, outputChars=len(output)),
        capabilitiesUsed=["chat"],
        chunks=None,
    )
    if idem_key:
        if len(_idempotency) >= 500:
            _idempotency.pop(next(iter(_idempotency)))
        _idempotency[idem_key] = response.model_dump(mode="json")
    return response


@app.post(
    "/internal/v1/tasks/claim",
    response_model=TaskClaimResponse,
    dependencies=[Depends(authenticate)],
)
def claim_task(request: TaskClaimRequest) -> TaskClaimResponse:
    cfg = _settings()
    live_tasks = [t for t in _tasks.values() if t["state"] == "claimed"]
    if len(live_tasks) >= cfg.max_tasks:
        raise HTTPException(status_code=status.HTTP_429_TOO_MANY_REQUESTS, detail="rate_limited")
    task_id = str(request.taskId) if request.taskId else str(uuid.uuid4())
    existing = _tasks.get(task_id)
    if existing is not None and existing["state"] == "claimed":
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="conflict")
    lease_id = str(uuid.uuid4())
    expires_at = _now_ms() + request.leaseMs
    _tasks[task_id] = {
        "lease_id": lease_id,
        "lease_expires_at_ms": expires_at,
        "state": "claimed",
    }
    return TaskClaimResponse(
        taskId=uuid.UUID(task_id),
        leaseId=uuid.UUID(lease_id),
        leaseExpiresAtMs=expires_at,
        state="claimed",
    )


@app.post(
    "/internal/v1/tasks/{task_id}/heartbeat",
    response_model=TaskHeartbeatResponse,
    dependencies=[Depends(authenticate)],
)
def heartbeat_task(task_id: uuid.UUID, request: TaskHeartbeatRequest) -> TaskHeartbeatResponse:
    entry = _get_task(str(task_id))
    _require_live_lease(entry, str(request.leaseId))
    expires_at = _now_ms() + request.leaseMs
    entry["lease_expires_at_ms"] = expires_at
    return TaskHeartbeatResponse(
        taskId=task_id,
        leaseId=request.leaseId,
        leaseExpiresAtMs=expires_at,
        state="claimed",
    )


@app.post(
    "/internal/v1/tasks/{task_id}/complete",
    response_model=TaskTerminalResponse,
    dependencies=[Depends(authenticate)],
)
def complete_task(task_id: uuid.UUID, request: TaskCompleteRequest) -> TaskTerminalResponse:
    entry = _get_task(str(task_id))
    _require_live_lease(entry, str(request.leaseId))
    entry["state"] = "completed"
    # Store only a short caller-supplied summary, never payload content.
    entry["result_summary"] = request.resultSummary[:256]
    digest = hashlib.sha256(request.resultSummary.encode("utf-8")).hexdigest()[:16]
    logger.info("Task complete task_id=%s summary_sha256_prefix=%s", task_id, digest)
    return TaskTerminalResponse(taskId=task_id, state="completed")


@app.post(
    "/internal/v1/tasks/{task_id}/fail",
    response_model=TaskTerminalResponse,
    dependencies=[Depends(authenticate)],
)
def fail_task(task_id: uuid.UUID, request: TaskFailRequest) -> TaskTerminalResponse:
    entry = _get_task(str(task_id))
    _require_live_lease(entry, str(request.leaseId))
    entry["state"] = "failed"
    entry["error"] = request.error[:256]
    logger.info("Task fail task_id=%s error=%s", task_id, request.error[:64])
    return TaskTerminalResponse(taskId=task_id, state="failed")
