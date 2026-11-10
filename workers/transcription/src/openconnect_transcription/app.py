"""Private HTTP API for the local transcription worker."""

from __future__ import annotations

import hmac
import logging
import os
import time
import uuid
from typing import Annotated, Any

from fastapi import Depends, FastAPI, Header, HTTPException, status

from openconnect_transcription.config import Settings
from openconnect_transcription import engine_mode
from openconnect_transcription.engine import (
    FakeTranscriptionEngine,
    advertised_capabilities,
    supported_sample_rates,
)
from openconnect_transcription.engine.audio import AudioValidationError, validate_audio
from openconnect_transcription.schemas import (
    CapabilityResponse,
    ExecuteRequest,
    ExecuteResponse,
    TaskClaimRequest,
    TaskClaimResponse,
    TaskCompleteRequest,
    TaskFailRequest,
    TaskHeartbeatRequest,
    TaskHeartbeatResponse,
    TaskTerminalResponse,
    TranscriptSegment,
)

logger = logging.getLogger("openconnect_transcription")
app = FastAPI(title="OpenConnect Transcription Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
engine = FakeTranscriptionEngine()

WORKER_VERSION = "0.1.0"

# In-memory broker state. Bounded; entries hold metadata only, never audio.
_tasks: dict[str, dict[str, Any]] = {}
_idempotency: dict[str, dict[str, Any]] = {}


def _now_ms() -> int:
    return int(time.time() * 1000)


def _settings() -> Settings:
    return settings


def _model_loaded(cfg: Settings) -> bool:
    if cfg.backend == "local-fake":
        return True
    manifest = os.path.join(cfg.model_dir, "model-manifest.json")
    return os.path.isfile(manifest)


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
    if cfg.backend == "local-model" and not _model_loaded(cfg):
        # Fail closed: a claimed local model without provisioned weights is not ready.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    if cfg.backend == "remote-endpoint" and not cfg.endpoint_url:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="endpoint_unavailable",
        )
    info, status_code, code = engine_mode.rejection(cfg.fake_engine, cfg.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    assert info is not None
    if not cfg.fake_engine and cfg.backend == "local-fake":
        # Real mode: the local-fake stub is unavailable without the explicit
        # fake flag; this build ships no real STT engine for this backend.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    return {
        "status": "ready",
        "backend": cfg.backend,
        "modelLoaded": _model_loaded(cfg),
        "capabilities": advertised_capabilities(backend=cfg.backend),
        "device": info.effective,
        "deviceRequested": info.requested,
    }


@app.get(
    "/internal/v1/capabilities",
    response_model=CapabilityResponse,
    dependencies=[Depends(authenticate)],
)
def capabilities() -> CapabilityResponse:
    cfg = _settings()
    return CapabilityResponse(
        worker="transcription",
        version=WORKER_VERSION,
        backend=cfg.backend,
        engine="fake-bounded" if cfg.fake_engine else "real",
        modelId=cfg.model_id,
        modelRevision=cfg.model_revision,
        modelLoaded=_model_loaded(cfg),
        capabilities=advertised_capabilities(backend=cfg.backend),
        privacyMode=cfg.privacy_mode,
        remoteBlocked=cfg.remote_blocked,
        supportsPartials=engine.supports_partials,
        sampleRatesHz=supported_sample_rates(),
        maxDurationMs=cfg.max_duration_ms,
    )


@app.post(
    "/internal/v1/execute",
    response_model=ExecuteResponse,
    dependencies=[Depends(authenticate)],
)
def execute(request: ExecuteRequest) -> ExecuteResponse:
    cfg = _settings()
    _, status_code, code = engine_mode.rejection(cfg.fake_engine, cfg.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    if cfg.remote_blocked:
        # Strict-local: unmasked audio must never leave the trusted boundary.
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail="privacy_blocked")
    if not cfg.fake_engine and cfg.backend == "local-fake":
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    if cfg.backend == "remote-endpoint":
        # This local build performs no outbound calls; a remote backend is
        # therefore unavailable rather than silently substituted.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="capability_unavailable",
        )
    if cfg.backend == "local-model" and not _model_loaded(cfg):
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    if request.input.partialsRequested and not engine.supports_partials:
        # No fabricated streaming: partials only when the engine supports them.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="capability_unavailable",
        )

    try:
        audio = validate_audio(
            audio_base64=request.input.audioBase64,
            mime_type=request.input.mimeType,
            sample_rate_hz=request.input.sampleRateHz,
            channels=request.input.channels,
            bits_per_sample=request.input.bitsPerSample,
            duration_ms=request.input.durationMs,
            max_audio_bytes=cfg.max_audio_bytes,
            max_duration_ms=cfg.max_duration_ms,
        )
    except AudioValidationError as exc:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST, detail="invalid_request"
        ) from exc

    idem_key = str(request.idempotencyKey) if request.idempotencyKey else None
    if idem_key and idem_key in _idempotency:
        return ExecuteResponse(**_idempotency[idem_key])

    result = engine.transcribe(audio, request.input.locale)

    # Log metadata only: never audio bytes, transcripts, or base64 payloads.
    logger.info(
        "Transcription execute request_id=%s backend=%s mime=%s rate=%d "
        "channels=%d bytes=%d duration_ms=%d locale=%s",
        request.requestId,
        cfg.backend,
        request.input.mimeType,
        audio.sample_rate_hz,
        audio.channels,
        len(audio.raw_pcm),
        audio.duration_ms,
        request.input.locale,
    )
    response = ExecuteResponse(
        schemaVersion="openconnect.v1",
        requestId=request.requestId,
        backend=cfg.backend,
        modelId=cfg.model_id,
        transcript=result.transcript,
        revision=result.revision,
        isFinal=result.is_final,
        locale=result.locale,
        durationMs=result.duration_ms,
        segments=[TranscriptSegment(startMs=s, endMs=e, text=t) for s, e, t in result.segments],
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
    entry["result_summary"] = request.resultSummary[:256]
    logger.info("Task complete task_id=%s", task_id)
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
