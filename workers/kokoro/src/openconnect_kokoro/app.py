"""Private HTTP API for the authenticated, local Kokoro TTS worker."""

from __future__ import annotations

import hmac
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from pydantic import ValidationError

from openconnect_kokoro.config import Settings
from openconnect_kokoro.engine import (
    IdempotencyConflict,
    KokoroEngine,
    ModelUnavailable,
    VoiceUnknown,
)
from openconnect_kokoro.schemas import EXECUTE_KINDS, ExecuteRequest, SynthesizePayload, TaskClaimRequest, TaskLeaseRequest
from openconnect_kokoro.tasks import LeaseConflict, TaskConflict, TaskNotFound, TaskRegistry

logger = logging.getLogger("openconnect_kokoro")
app = FastAPI(title="OpenConnect Kokoro Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
tasks = TaskRegistry(frozenset(EXECUTE_KINDS))
synth_engine = KokoroEngine(settings.model_dir)


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    try:
        synth_engine.ready()
    except ModelUnavailable as exc:
        raise HTTPException(status_code=503, detail="model_unavailable") from exc
    return {
        "status": "ready",
        "modelReady": True,
        "defaultVoice": settings.default_voice,
        "capabilities": ["speech.synthesize"],
        "device": "cpu",
        "deviceRequested": "cpu",
        "modelCacheDir": settings.model_dir,
    }


def authenticate(authorization: Annotated[str | None, Header()] = None) -> None:
    token = settings.worker_token
    supplied = authorization.removeprefix("Bearer ") if authorization else ""
    if token is None or not authorization or not authorization.startswith("Bearer ") or not hmac.compare_digest(supplied, token):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="unauthorized")


@app.get("/internal/v1/capabilities", dependencies=[Depends(authenticate)])
def capabilities() -> dict[str, object]:
    return {
        "schemaVersion": "openconnect.v1",
        "worker": "kokoro",
        "engine": KokoroEngine.engine,
        "engineVersion": KokoroEngine.version,
        "capabilities": ["speech.synthesize"],
        "voices": ["af_heart"],
        "defaultVoice": "af_heart",
        "nativeSampleRateHz": 24000,
        "nativeChannels": 1,
        "formats": ["wav", "pcm"],
        "executeKinds": list(EXECUTE_KINDS),
    }


@app.post("/internal/v1/execute", dependencies=[Depends(authenticate)])
def execute(request: ExecuteRequest) -> dict[str, object]:
    if request.kind != "speech.synthesize":
        raise HTTPException(status_code=422, detail="unknown kind")
    try:
        body = SynthesizePayload(**request.payload)
        response, replayed = synth_engine.synthesize(
            text=body.text,
            voice_id=body.voiceId or "af_heart",
            locale=body.locale,
            audio_format=body.format,
            idempotency_key=body.idempotencyKey or request.idempotencyKey,
        )
    except ValidationError as exc:
        raise HTTPException(status_code=422, detail="invalid payload") from exc
    except VoiceUnknown as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except ModelUnavailable as exc:
        raise HTTPException(status_code=503, detail="model_unavailable") from exc
    except IdempotencyConflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
    if body.speechEpoch is not None:
        response["speechEpoch"] = body.speechEpoch
    return {"schemaVersion": "openconnect.v1", "taskId": request.taskId, "kind": request.kind, "idempotentReplay": replayed, "result": response}


def _lease(request: TaskLeaseRequest) -> tuple[str, int]:
    return str(request.leaseId), request.fencingToken


@app.post("/internal/v1/tasks/claim", dependencies=[Depends(authenticate)])
def claim_task(request: TaskClaimRequest) -> dict[str, object]:
    try:
        return tasks.claim(request.taskId, request.kind)
    except TaskConflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@app.get("/internal/v1/tasks/{task_id}", dependencies=[Depends(authenticate)])
def get_task(task_id: str) -> dict[str, object]:
    try:
        return tasks.get(task_id)
    except TaskNotFound as exc:
        raise HTTPException(status_code=404, detail="task not found") from exc


@app.post("/internal/v1/tasks/{task_id}/heartbeat", dependencies=[Depends(authenticate)])
def heartbeat_task(task_id: str, request: TaskLeaseRequest) -> dict[str, object]:
    lease_id, fencing = _lease(request)
    try:
        return tasks.heartbeat(task_id, lease_id, fencing)
    except TaskNotFound as exc:
        raise HTTPException(status_code=404, detail="task not found") from exc
    except LeaseConflict as exc:
        raise HTTPException(status_code=409, detail=f"stale fencing token, expected {exc.expected_fencing_token}") from exc
    except TaskConflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@app.post("/internal/v1/tasks/{task_id}/complete", dependencies=[Depends(authenticate)])
def complete_task(task_id: str, request: TaskLeaseRequest) -> dict[str, object]:
    lease_id, fencing = _lease(request)
    try:
        return tasks.complete(task_id, lease_id, fencing, request.result)
    except TaskNotFound as exc:
        raise HTTPException(status_code=404, detail="task not found") from exc
    except LeaseConflict as exc:
        raise HTTPException(status_code=409, detail=f"stale fencing token, expected {exc.expected_fencing_token}") from exc
    except TaskConflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


@app.post("/internal/v1/tasks/{task_id}/fail", dependencies=[Depends(authenticate)])
def fail_task(task_id: str, request: TaskLeaseRequest) -> dict[str, object]:
    lease_id, fencing = _lease(request)
    try:
        return tasks.fail(task_id, lease_id, fencing, request.error)
    except TaskNotFound as exc:
        raise HTTPException(status_code=404, detail="task not found") from exc
    except LeaseConflict as exc:
        raise HTTPException(status_code=409, detail=f"stale fencing token, expected {exc.expected_fencing_token}") from exc
    except TaskConflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
