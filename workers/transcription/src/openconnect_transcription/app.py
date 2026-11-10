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
from openconnect_transcription import cloud as cloud_module
from openconnect_transcription import engine_mode
from openconnect_transcription.engine import (
    FakeTranscriptionEngine,
    TranscriptResult,
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


class _LocalLegUnavailable(RuntimeError):
    """Local leg cannot serve this request."""


def _routing_status(cfg: Settings) -> dict[str, object]:
    return {
        "routeOrder": cfg.route_order,
        "cloudConfigured": cfg.cloud_url is not None,
        "cloudAvailable": _cloud_leg_available(cfg),
        "localAvailable": _local_leg_available(cfg),
    }


def _cloud_leg_available(cfg: Settings) -> bool:
    # The cloud leg needs an exact provider model ID: OC_TRANSCRIPTION_MODEL
    # defaults to empty (never guessed), so the cloud path stays unready
    # until the operator sets it -- even with URL + key configured.
    return (
        cfg.cloud_url is not None
        and cfg.cloud_api_key is not None
        and cfg.model_id is not None
        and cfg.model_id != ""
        and not cloud_module.privacy_zone_strict()
    )


def _local_leg_available(cfg: Settings) -> bool:
    # This build provisions a local adapter only via local-model + manifest.
    return cfg.backend == "local-model" and _model_loaded(cfg)


def _ordered_legs(cfg: Settings) -> list[str]:
    if cfg.cloud_url is None:
        return ["local"]
    if cfg.route_order == cloud_module.LOCAL_FIRST:
        return ["local", "cloud"]
    return ["cloud", "local"]


def _try_local_real(cfg: Settings, audio, locale: str) -> TranscriptResult:
    """Local leg: serves only a provisioned local-model checkpoint."""
    if cfg.backend == "local-model" and _model_loaded(cfg):
        return engine.transcribe(audio, locale)
    raise _LocalLegUnavailable("no provisioned local model for this request")


def _try_cloud_real(cfg: Settings, audio, request: ExecuteRequest) -> TranscriptResult:
    """Cloud leg. Raises CloudPolicyDenied (terminal) or CloudLegFailed."""
    assert cfg.cloud_url is not None and cfg.cloud_api_key is not None
    if cloud_module.privacy_zone_strict():
        # Terminal: strict-local disables the cloud path entirely.
        raise cloud_module.CloudPolicyDenied("privacy_blocked")
    payload = {
        "audioBase64": request.input.audioBase64,
        "mimeType": request.input.mimeType,
        "sampleRateHz": request.input.sampleRateHz,
        "channels": request.input.channels,
        "bitsPerSample": request.input.bitsPerSample,
        "durationMs": request.input.durationMs,
        "locale": request.input.locale,
    }
    response = cloud_module.post_json(
        url=cfg.cloud_url,
        payload=payload,
        api_key=cfg.cloud_api_key,
        timeout_s=cfg.cloud_timeout_ms / 1000.0,
    )
    transcript = response.get("transcript")
    if not isinstance(transcript, str) or not transcript:
        raise cloud_module.CloudLegFailed("cloud leg returned no transcript")
    revision = response.get("revision", 1)
    raw_segments = response.get("segments")
    segments: list[tuple[int, int, str]] = [(0, audio.duration_ms, transcript)]
    if isinstance(raw_segments, list) and raw_segments:
        parsed: list[tuple[int, int, str]] = []
        for entry in raw_segments:
            if isinstance(entry, (list, tuple)) and len(entry) == 3 and isinstance(entry[2], str):
                parsed.append((int(entry[0]), int(entry[1]), entry[2]))
        if parsed:
            segments = parsed
    return TranscriptResult(
        transcript=transcript,
        revision=revision if isinstance(revision, int) else 1,
        is_final=True,
        locale=request.input.locale,
        duration_ms=audio.duration_ms,
        segments=segments,
    )


def _execute_real_routed(cfg: Settings, audio, request: ExecuteRequest) -> TranscriptResult:
    """Route across cloud/local legs. Exhaustion -> 503 model_unavailable."""
    for leg in _ordered_legs(cfg):
        try:
            if leg == "cloud":
                if cfg.cloud_url is None:
                    continue
                return _try_cloud_real(cfg, audio, request)
            return _try_local_real(cfg, audio, request.input.locale)
        except cloud_module.CloudPolicyDenied as exc:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=exc.code
            ) from exc
        except (cloud_module.CloudLegFailed, _LocalLegUnavailable) as exc:
            logger.info("Real leg %s failed; trying next leg outcome=%s", leg, type(exc).__name__)
            continue
    raise HTTPException(status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable")


@app.get("/health/ready")
def ready() -> dict[str, object]:
    cfg = _settings()
    # Backend provisioning gates apply in both modes (claimed backends must
    # be provisioned); a configured cloud leg only yields the
    # missing-weights gates, never a missing-URL config error.
    if cfg.backend == "local-model" and not _model_loaded(cfg) and cfg.cloud_url is None:
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
    if cfg.fake_engine:
        return {
            "status": "ready",
            "backend": cfg.backend,
            "modelLoaded": _model_loaded(cfg),
            "capabilities": advertised_capabilities(backend=cfg.backend),
            "device": info.effective,
            "deviceRequested": info.requested,
            "provisioning": cfg.provisioning,
            "modelCacheDir": cfg.model_dir,
            "transcriptionProvider": cfg.transcription_provider,
            "transcriptionMode": cfg.transcription_mode,
            "transcriptionModel": cfg.model_id,
            **_routing_status(cfg),
        }
    # Real mode: ready iff a serving leg exists (opt-in cloud, or a
    # provisioned local-model checkpoint). Both down -> typed unready.
    # (Readiness reports availability; per-request policy verdicts such as
    # strict-local privacy_blocked surface on execute.)
    if not (_cloud_leg_available(cfg) or _local_leg_available(cfg)):
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
        "provisioning": cfg.provisioning,
        "modelCacheDir": cfg.model_dir,
        "transcriptionProvider": cfg.transcription_provider,
        "transcriptionMode": cfg.transcription_mode,
        "transcriptionModel": cfg.model_id,
        **_routing_status(cfg),
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
    if cfg.backend == "remote-endpoint":
        # This local build performs no outbound calls; a remote backend is
        # therefore unavailable rather than silently substituted.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="capability_unavailable",
        )
    if cfg.backend == "local-model" and not _model_loaded(cfg) and cfg.cloud_url is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    if not cfg.fake_engine and cfg.backend == "local-fake" and cfg.cloud_url is None:
        # No serving leg: the local-fake stub never serves in real mode.
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

    if cfg.fake_engine:
        result = engine.transcribe(audio, request.input.locale)
    else:
        result = _execute_real_routed(cfg, audio, request)

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
