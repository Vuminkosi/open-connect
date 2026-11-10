"""Private HTTP API for the MMS TTS worker."""

from __future__ import annotations

import hmac
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from pydantic import ValidationError

from openconnect_mms_tts.config import Settings
from openconnect_mms_tts import engine_mode
from openconnect_mms_tts.engine import (
    CheckpointCache,
    FakeMmsEngine,
    LanguageNotOptedIn,
    LicenseBlocked,
    Misconfigured,
)
from openconnect_mms_tts.schemas import (
    EXECUTE_KINDS,
    ExecuteRequest,
    MmsSynthesizePayload,
    TaskClaimRequest,
    TaskLeaseRequest,
)
from openconnect_mms_tts.tasks import (
    LeaseConflict,
    TaskConflict,
    TaskNotFound,
    TaskRegistry,
)

logger = logging.getLogger("openconnect_mms_tts")
app = FastAPI(title="OpenConnect MMS TTS Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
checkpoints = CheckpointCache(settings.cache_size)
tts_engine = FakeMmsEngine(checkpoints)
tasks = TaskRegistry(frozenset(EXECUTE_KINDS))


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    info, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    assert info is not None
    error = settings.configuration_error()
    if error is not None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="worker misconfigured",
        )
    if not settings.fake_engine:
        # Real mode: no real MMS checkpoints are provisioned in this build.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    return {
        "status": "ready",
        "optInLanguages": list(settings.languages),
        "commercialUse": settings.commercial_use,
        "capabilities": ["mms.synthesize"],
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
    # Only opted-in checkpoints are advertised; the full catalogue is never listed.
    return {
        "schemaVersion": "openconnect.v1",
        "worker": "mms-tts",
        "engine": FakeMmsEngine.engine if settings.fake_engine else "real",
        "engineVersion": FakeMmsEngine.version,
        "capabilities": ["mms.synthesize"],
        "optInLanguages": [
            {"language": lang, "license": settings.license_for(lang)} for lang in settings.languages
        ],
        "commercialUse": settings.commercial_use,
        "executeKinds": list(EXECUTE_KINDS),
    }


@app.post("/internal/v1/execute", dependencies=[Depends(authenticate)])
def execute(request: ExecuteRequest) -> dict[str, object]:
    _, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    if request.kind != "mms.synthesize":
        raise HTTPException(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, detail="unknown kind")
    try:
        body = MmsSynthesizePayload(**request.payload)
    except ValidationError as exc:
        raise HTTPException(status_code=422, detail="invalid payload") from exc
    if not settings.fake_engine:
        # Real mode: no real MMS checkpoints are provisioned in this build.
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="model_unavailable"
        )
    try:
        response = tts_engine.synthesize(
            text=body.text,
            language=body.language.lower(),
            opt_in_languages=settings.languages,
            license_for=settings.license_for(body.language.lower()),
            commercial_use=settings.commercial_use,
        )
    except LanguageNotOptedIn as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, detail=str(exc)
        ) from exc
    except LicenseBlocked as exc:
        raise HTTPException(status_code=status.HTTP_403_FORBIDDEN, detail=str(exc)) from exc
    except Misconfigured as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="worker misconfigured",
        ) from exc
    if body.speechEpoch is not None:
        response["speechEpoch"] = body.speechEpoch
    return {
        "schemaVersion": "openconnect.v1",
        "taskId": request.taskId,
        "kind": request.kind,
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
