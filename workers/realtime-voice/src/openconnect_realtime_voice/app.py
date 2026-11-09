"""Private HTTP API for the realtime-voice worker."""

from __future__ import annotations

import hmac
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from pydantic import ValidationError

from openconnect_realtime_voice import engine as engine_module
from openconnect_realtime_voice.config import Settings
from openconnect_realtime_voice.schemas import (
    EXECUTE_KINDS,
    ExecuteRequest,
    SessionOpenRequest,
    TaskClaimRequest,
    TaskLeaseRequest,
    VoiceEventRequest,
)
from openconnect_realtime_voice.sessions import (
    InvalidTransition,
    SessionNotFound,
    SessionRegistry,
    StaleEpoch,
)
from openconnect_realtime_voice.tasks import (
    LeaseConflict,
    TaskConflict,
    TaskNotFound,
    TaskRegistry,
)

logger = logging.getLogger("openconnect_realtime_voice")
app = FastAPI(title="OpenConnect Realtime Voice Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
sessions = SessionRegistry()
tasks = TaskRegistry(frozenset(EXECUTE_KINDS))
voice_engine = engine_module.FakeVoiceEngine(sessions)


def _capabilities() -> list[str]:
    if settings.orchestration_only:
        return ["voice.session", "voice.barge-in", "voice.fake-chunks"]
    return ["voice.session", "voice.barge-in", "voice.fake-chunks", "voice.local-vad"]


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    # Orchestration-only mode needs no local weights: ready without model init.
    return {
        "status": "ready",
        "mode": settings.mode,
        "capabilities": _capabilities(),
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
    return {
        "schemaVersion": "openconnect.v1",
        "worker": "realtime-voice",
        "engine": engine_module.FakeVoiceEngine.engine,
        "engineVersion": engine_module.FakeVoiceEngine.version,
        "mode": settings.mode,
        "capabilities": _capabilities(),
        "executeKinds": list(EXECUTE_KINDS),
    }


def _session_payload(session_id: str) -> dict[str, object]:
    try:
        session = sessions.get(session_id)
    except SessionNotFound as exc:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND, detail="session not found"
        ) from exc
    return {
        "sessionId": session.session_id,
        "state": session.state,
        "speechEpoch": session.speech_epoch,
    }


@app.post("/internal/v1/execute", dependencies=[Depends(authenticate)])
def execute(request: ExecuteRequest) -> dict[str, object]:
    if request.kind == "voice.session-open":
        try:
            body = SessionOpenRequest(**request.payload)
        except ValidationError as exc:
            raise HTTPException(status_code=422, detail="invalid payload") from exc
        session = sessions.open(str(body.sessionId))
        return {
            "schemaVersion": "openconnect.v1",
            "taskId": request.taskId,
            "kind": request.kind,
            "state": session.state,
            "speechEpoch": session.speech_epoch,
        }
    if request.kind == "voice.event":
        try:
            body = VoiceEventRequest(**request.payload)
        except ValidationError as exc:
            raise HTTPException(status_code=422, detail="invalid payload") from exc
        try:
            session, detail = sessions.apply_event(
                str(body.sessionId),
                body.type,
                speech_epoch=body.speechEpoch,
                text=body.text,
            )
        except SessionNotFound as exc:
            raise HTTPException(
                status_code=status.HTTP_404_NOT_FOUND, detail="session not found"
            ) from exc
        except InvalidTransition as exc:
            raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail=str(exc)) from exc
        except StaleEpoch as exc:
            # Stale-epoch audio work is discarded, never applied.
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail=f"stale speech epoch, expected {exc.expected}",
            ) from exc
        result: dict[str, object] = {
            "sessionId": str(body.sessionId),
            "state": session.state,
            "speechEpoch": session.speech_epoch,
        }
        result.update(detail)
        if body.type == "begin-speaking":
            result["chunks"] = voice_engine.synthesize_chunks(
                body.text or "", body.voice or "default", body.language or "en"
            )
        return {
            "schemaVersion": "openconnect.v1",
            "taskId": request.taskId,
            "kind": request.kind,
            "result": result,
        }
    raise HTTPException(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, detail="unknown kind")


@app.get("/internal/v1/voice/sessions/{session_id}", dependencies=[Depends(authenticate)])
def get_session(session_id: str) -> dict[str, object]:
    return _session_payload(session_id)


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
