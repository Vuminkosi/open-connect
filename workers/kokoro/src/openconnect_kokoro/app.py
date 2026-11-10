"""Private HTTP API for the Kokoro TTS worker."""

from __future__ import annotations

import hmac
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from pydantic import ValidationError

from openconnect_kokoro.config import Settings
from openconnect_kokoro import engine_mode
from openconnect_kokoro.engine import (
    FakeKokoroEngine,
    IdempotencyConflict,
    ModelUnavailable,
    VoiceUnknown,
)
from openconnect_kokoro.schemas import (
    EXECUTE_KINDS,
    ExecuteRequest,
    SynthesizePayload,
    TaskClaimRequest,
    TaskLeaseRequest,
)
from openconnect_kokoro.tasks import (
    LeaseConflict,
    TaskConflict,
    TaskNotFound,
    TaskRegistry,
)

logger = logging.getLogger("openconnect_kokoro")
app = FastAPI(title="OpenConnect Kokoro Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
tasks = TaskRegistry(frozenset(EXECUTE_KINDS))
synth_engine = FakeKokoroEngine()


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    info, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    assert info is not None
    if not settings.fake_engine:
        # Real mode: this build ships no real Kokoro weights, so without the
        # explicit fake flag the worker is unready until a real engine lands.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    model_ready = settings.model_marker_present()
    if settings.require_model and not model_ready:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model not staged"
        )
    advertised = list(settings.voices) if (model_ready or not settings.require_model) else []
    return {
        "status": "ready",
        "modelReady": model_ready,
        "defaultVoice": settings.default_voice,
        "capabilities": ["speech.synthesize"] if advertised else [],
        "device": info.effective,
        "deviceRequested": info.requested,
    }


def authenticate(authorization: Annotated[str | None, Header()] = None) -> None:
    token = settings.worker_token
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


@app.get("/internal/v1/capabilities", dependencies=[Depends(authenticate)])
def capabilities() -> dict[str, object]:
    # Never advertise voices that are not verified in the inventory.
    return {
        "schemaVersion": "openconnect.v1",
        "worker": "kokoro",
        "engine": FakeKokoroEngine.engine if settings.fake_engine else "real",
        "engineVersion": FakeKokoroEngine.version,
        "capabilities": ["speech.synthesize"],
        "voices": list(settings.voices),
        "defaultVoice": settings.default_voice,
        "nativeSampleRateHz": 24000,
        "nativeChannels": 1,
        "formats": ["wav", "mp3", "pcm"],
        "executeKinds": list(EXECUTE_KINDS),
    }


@app.post("/internal/v1/execute", dependencies=[Depends(authenticate)])
def execute(request: ExecuteRequest) -> dict[str, object]:
    _, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    if not settings.fake_engine:
        # Real mode: no real Kokoro weights are provisioned in this build.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    if request.kind != "speech.synthesize":
        raise HTTPException(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, detail="unknown kind")
    try:
        body = SynthesizePayload(**request.payload)
    except ValidationError as exc:
        raise HTTPException(status_code=422, detail="invalid payload") from exc
    voice_id = body.voiceId or settings.default_voice
    key = body.idempotencyKey or request.idempotencyKey
    try:
        response, replayed = synth_engine.synthesize(
            text=body.text,
            voice_id=voice_id,
            locale=body.locale,
            audio_format=body.format,
            voices=settings.voices,
            model_ready=settings.model_marker_present(),
            require_model=settings.require_model,
            idempotency_key=key,
        )
    except VoiceUnknown as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, detail=str(exc)
        ) from exc
    except ModelUnavailable as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=str(exc)
        ) from exc
    except IdempotencyConflict as exc:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc
    if body.speechEpoch is not None:
        response["speechEpoch"] = body.speechEpoch
    return {
        "schemaVersion": "openconnect.v1",
        "taskId": request.taskId,
        "kind": request.kind,
        "idempotentReplay": replayed,
        "result": response,
    }


@app.post("/internal/v1/tasks/claim", dependencies=[Depends(authenticate)])
def claim_task(request: TaskClaimRequest) -> dict[str, object]:
    try:
        return tasks.claim(request.taskId, request.kind)
    except TaskConflict as exc:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc


@app.get("/internal/v1/tasks/{task_id}", dependencies=[Depends(authenticate)])
def get_task(task_id: str) -> dict[str, object]:
    try:
        return tasks.get(task_id)
    except TaskNotFound as exc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="task not found") from exc


def _lease(request: TaskLeaseRequest) -> tuple[str, int]:
    return str(request.leaseId), request.fencingToken


@app.post("/internal/v1/tasks/{task_id}/heartbeat", dependencies=[Depends(authenticate)])
def heartbeat_task(task_id: str, request: TaskLeaseRequest) -> dict[str, object]:
    lease_id, fencing = _lease(request)
    try:
        return tasks.heartbeat(task_id, lease_id, fencing)
    except TaskNotFound as exc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="task not found") from exc
    except LeaseConflict as exc:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail=f"stale fencing token, expected {exc.expected_fencing_token}",
        ) from exc
    except TaskConflict as exc:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc


@app.post("/internal/v1/tasks/{task_id}/complete", dependencies=[Depends(authenticate)])
def complete_task(task_id: str, request: TaskLeaseRequest) -> dict[str, object]:
    lease_id, fencing = _lease(request)
    try:
        return tasks.complete(task_id, lease_id, fencing, request.result)
    except TaskNotFound as exc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="task not found") from exc
    except LeaseConflict as exc:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail=f"stale fencing token, expected {exc.expected_fencing_token}",
        ) from exc
    except TaskConflict as exc:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc


@app.post("/internal/v1/tasks/{task_id}/fail", dependencies=[Depends(authenticate)])
def fail_task(task_id: str, request: TaskLeaseRequest) -> dict[str, object]:
    lease_id, fencing = _lease(request)
    try:
        return tasks.fail(task_id, lease_id, fencing, request.error)
    except TaskNotFound as exc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="task not found") from exc
    except LeaseConflict as exc:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail=f"stale fencing token, expected {exc.expected_fencing_token}",
        ) from exc
    except TaskConflict as exc:
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc
