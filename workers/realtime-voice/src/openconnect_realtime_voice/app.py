"""Private HTTP API for the realtime-voice worker."""

from __future__ import annotations

import hmac
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from pydantic import ValidationError

from openconnect_realtime_voice import engine as engine_module
from openconnect_realtime_voice import engine_mode
from openconnect_realtime_voice.config import Settings
from openconnect_realtime_voice.observations import (
    EndpointEvaluator,
    ObservationRegistry,
    StaleObservationEpoch,
)
from openconnect_realtime_voice.schemas import (
    EXECUTE_KINDS,
    EndpointEvaluateRequest,
    ExecuteRequest,
    SessionOpenRequest,
    TaskClaimRequest,
    TaskLeaseRequest,
    VoiceEventRequest,
    VoiceObserveRequest,
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
endpoint_evaluator = EndpointEvaluator()
observations = ObservationRegistry(endpoint_evaluator)
tasks = TaskRegistry(frozenset(EXECUTE_KINDS))
voice_engine = engine_module.FakeVoiceEngine(sessions)


def _capabilities() -> list[str]:
    capabilities = [
        "voice.session",
        "voice.barge-in",
        "voice.observe",
        "voice.endpoint.evaluate",
    ]
    if settings.fake_engine:
        # Deterministic fake chunks exist only when the fake engine is on.
        capabilities.append("voice.fake-chunks")
    if not settings.orchestration_only:
        capabilities.append("voice.local-vad")
    return capabilities


def _engine_label() -> str:
    return engine_module.FakeVoiceEngine.engine if settings.fake_engine else "real"


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    info, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    assert info is not None
    # Orchestration-only mode needs no local weights: session orchestration is
    # ready in real mode too; only fake chunk synthesis requires the fake flag.
    return {
        "status": "ready",
        "mode": settings.mode,
        "engine": _engine_label(),
        "capabilities": _capabilities(),
        "device": info.effective,
        "deviceRequested": info.requested,
        "provisioning": settings.provisioning,
        "modelCacheDir": settings.model_dir,
        "transcriptionMode": settings.transcription_mode,
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
        "engine": _engine_label(),
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
    _, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
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
        if body.type == "begin-speaking" and not settings.fake_engine:
            # Real mode: orchestration applies, but fake chunk synthesis is
            # unavailable without the explicit fake flag. Reject before any
            # session mutation so failed synthesis never moves session state.
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="model_unavailable",
            )
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
    if request.kind == "voice.observe":
        try:
            body = VoiceObserveRequest(**request.payload)
        except ValidationError as exc:
            raise HTTPException(status_code=422, detail="invalid payload") from exc
        if not body.sessionId:
            raise HTTPException(status_code=422, detail="sessionId is required")
        try:
            snapshot, endpoint = observations.observe(
                tenant_id=body.tenantId,
                session_id=body.sessionId,
                speech_epoch=body.speechEpoch,
                capture_id=body.captureId,
                connection_state=body.connectionState,
                input_state=body.inputState,
                reasoning_state=body.reasoningState,
                output_state=body.outputState,
                silence_ms=body.silenceMs,
                speech_duration_ms=body.speechDurationMs,
                transcript=body.transcript,
                stable_text=body.stableText,
                unstable_text=body.unstableText,
                transcript_revision=body.transcriptRevision,
                assistant_speaking=body.assistantSpeaking,
                endpointing_mode=body.endpointingMode,
                evaluate_endpoint=body.evaluateEndpoint,
            )
        except StaleObservationEpoch as exc:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail=f"stale speech epoch, expected >= {exc.expected}",
            ) from exc
        observe_result: dict[str, object] = {
            "observation": snapshot.to_dict(),
            "authoritative": False,
        }
        if endpoint is not None:
            observe_result["endpointObservation"] = endpoint.to_dict()
        return {
            "schemaVersion": "openconnect.v1",
            "taskId": request.taskId,
            "kind": request.kind,
            "result": observe_result,
        }
    if request.kind == "voice.endpoint.evaluate":
        try:
            eval_body = EndpointEvaluateRequest(**request.payload)
        except ValidationError as exc:
            raise HTTPException(status_code=422, detail="invalid payload") from exc
        try:
            snapshot, endpoint = observations.observe(
                tenant_id=eval_body.tenantId,
                session_id=eval_body.sessionId,
                speech_epoch=eval_body.speechEpoch,
                capture_id=eval_body.captureId,
                output_state=eval_body.outputState,
                silence_ms=eval_body.silenceMs,
                speech_duration_ms=eval_body.speechDurationMs,
                transcript=eval_body.transcript,
                stable_text=eval_body.stableText,
                unstable_text=eval_body.unstableText,
                transcript_revision=eval_body.transcriptRevision,
                assistant_speaking=eval_body.assistantSpeaking,
                endpointing_mode=eval_body.endpointingMode,
                evaluate_endpoint=True,
            )
        except StaleObservationEpoch as exc:
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail=f"stale speech epoch, expected >= {exc.expected}",
            ) from exc
        assert endpoint is not None
        return {
            "schemaVersion": "openconnect.v1",
            "taskId": request.taskId,
            "kind": request.kind,
            "result": {
                "observation": snapshot.to_dict(),
                "endpointObservation": endpoint.to_dict(),
                "authoritative": False,
            },
        }
    raise HTTPException(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, detail="unknown kind")


@app.post("/internal/v2/sessions/{session_id}/observe", dependencies=[Depends(authenticate)])
def observe_session_v2(session_id: str, request: VoiceObserveRequest) -> dict[str, object]:
    _, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(status_code=status_code or 503, detail=code)
    if request.sessionId is not None and request.sessionId != session_id:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="sessionId mismatch",
        )
    try:
        snapshot, endpoint = observations.observe(
            tenant_id=request.tenantId,
            session_id=session_id,
            speech_epoch=request.speechEpoch,
            capture_id=request.captureId,
            connection_state=request.connectionState,
            input_state=request.inputState,
            reasoning_state=request.reasoningState,
            output_state=request.outputState,
            silence_ms=request.silenceMs,
            speech_duration_ms=request.speechDurationMs,
            transcript=request.transcript,
            stable_text=request.stableText,
            unstable_text=request.unstableText,
            transcript_revision=request.transcriptRevision,
            assistant_speaking=request.assistantSpeaking,
            endpointing_mode=request.endpointingMode,
            evaluate_endpoint=request.evaluateEndpoint,
        )
    except StaleObservationEpoch as exc:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail=f"stale speech epoch, expected >= {exc.expected}",
        ) from exc
    response: dict[str, object] = {
        "schemaVersion": "openconnect.v2",
        "observation": snapshot.to_dict(),
        "authoritative": False,
    }
    if endpoint is not None:
        response["endpointObservation"] = endpoint.to_dict()
    return response


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
