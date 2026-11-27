"""Private HTTP/WebSocket API for the authenticated, local Kokoro TTS worker."""

from __future__ import annotations

import asyncio
import hmac
import json
import logging
import os
import threading
from typing import Annotated
from uuid import UUID

from fastapi import Depends, FastAPI, Header, HTTPException, Request, WebSocket, WebSocketDisconnect, status
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from pydantic import ValidationError

from openconnect_kokoro.cloud_execution import configured_client, execute_cloud
from openconnect_kokoro.config import Settings
from openconnect_kokoro.engine import (
    IdempotencyConflict,
    KokoroEngine,
    ModelUnavailable,
    NATIVE_CHANNELS,
    NATIVE_SAMPLE_RATE_HZ,
    OverloadError,
    VoiceUnknown,
)
from openconnect_kokoro.schemas import (
    EXECUTE_KINDS,
    ExecuteRequest,
    StreamCancelRequest,
    StreamSynthesizeRequest,
    SynthesizePayload,
    TaskClaimRequest,
    TaskLeaseRequest,
)
from openconnect_kokoro.tasks import LeaseConflict, TaskConflict, TaskNotFound, TaskRegistry

MAX_WS_CONTROL_BYTES = 16 * 1024
MAX_BINARY_FRAME_BYTES = 96 * 1024

logger = logging.getLogger("openconnect_kokoro")
app = FastAPI(title="OpenConnect Kokoro Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
tasks = TaskRegistry(frozenset(EXECUTE_KINDS))
synth_engine = KokoroEngine(
    settings.model_dir,
    max_pending_streams=settings.max_pending_streams,
    inference_wait_timeout_seconds=settings.inference_wait_timeout_seconds,
)


def _advertised_capabilities() -> list[str]:
    caps = ["speech.synthesize"]
    try:
        if configured_client() is None:
            caps.append("speech.synthesize.stream")
    except Exception:
        pass
    return caps


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
        "capabilities": _advertised_capabilities(),
        "device": "cpu",
        "deviceRequested": "cpu",
        "modelCacheDir": settings.model_dir,
    }


def _is_authorized(authorization: str | None) -> bool:
    token = settings.worker_token
    supplied = authorization.removeprefix("Bearer ") if authorization else ""
    return (
        token is not None
        and bool(authorization)
        and authorization.startswith("Bearer ")
        and hmac.compare_digest(supplied, token)
    )


def authenticate(authorization: Annotated[str | None, Header()] = None) -> None:
    if not _is_authorized(authorization):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="unauthorized")


async def _deny_websocket(
    websocket: WebSocket,
    status_code: int,
    detail: str,
    ws_close_code: int,
) -> None:
    if "websocket.http.response" in websocket.scope.get("extensions", {}):
        await websocket.send_denial_response(
            JSONResponse(status_code=status_code, content={"detail": detail})
        )
        return
    await websocket.close(code=ws_close_code, reason=detail)


@app.get("/internal/cloud/v1/voice-synthesis/ready", dependencies=[Depends(authenticate)])
def cloud_ready() -> dict[str, str]:
    try:
        deployment = UUID(os.environ.get("OPENCONNECT_CLOUD_DEPLOYMENT_ID", ""))
        if deployment.int == 0 or configured_client() is None:
            raise ValueError("cloud execution unavailable")
        synth_engine.ready()
        return {"status": "ready", "deploymentId": str(deployment)}
    except Exception:
        raise HTTPException(status_code=503, detail="execution unavailable") from None


@app.get("/internal/v1/capabilities", dependencies=[Depends(authenticate)])
def capabilities() -> dict[str, object]:
    return {
        "schemaVersion": "openconnect.v1",
        "worker": "kokoro",
        "engine": KokoroEngine.engine,
        "engineVersion": KokoroEngine.version,
        "capabilities": _advertised_capabilities(),
        "voices": list(settings.voices),
        "defaultVoice": settings.default_voice,
        "nativeSampleRateHz": NATIVE_SAMPLE_RATE_HZ,
        "nativeChannels": NATIVE_CHANNELS,
        "formats": ["wav", "pcm"],
        "executeKinds": list(EXECUTE_KINDS),
    }


@app.websocket("/internal/v2/speech/stream")
async def speech_stream(websocket: WebSocket) -> None:
    if not _is_authorized(websocket.headers.get("authorization")):
        await _deny_websocket(websocket, status.HTTP_401_UNAUTHORIZED, "unauthorized", 1008)
        return

    try:
        cloud_active = configured_client() is not None
    except Exception:
        await _deny_websocket(
            websocket, status.HTTP_503_SERVICE_UNAVAILABLE, "cloud_streaming_unsupported", 1013
        )
        return
    if cloud_active:
        await _deny_websocket(
            websocket, status.HTTP_503_SERVICE_UNAVAILABLE, "cloud_streaming_unsupported", 1013
        )
        return

    await websocket.accept()

    send_lock = asyncio.Lock()
    disconnected = False
    active_task: asyncio.Task[None] | None = None
    active_cancel: threading.Event | None = None
    active_request: StreamSynthesizeRequest | None = None

    async def send_json(payload: dict[str, object]) -> bool:
        nonlocal disconnected
        async with send_lock:
            if disconnected:
                return False
            try:
                await websocket.send_json(payload)
                return True
            except (WebSocketDisconnect, RuntimeError):
                disconnected = True
                return False

    async def send_bytes(data: bytes) -> bool:
        nonlocal disconnected
        async with send_lock:
            if disconnected:
                return False
            try:
                await websocket.send_bytes(data)
                return True
            except (WebSocketDisconnect, RuntimeError):
                disconnected = True
                return False

    async def send_error(
        code: str,
        http_status: int,
        message: str,
        *,
        response_id: str = "",
        segment_id: str = "",
        speech_epoch: int = 0,
        total_samples: int = 0,
    ) -> None:
        await send_json(
            {
                "type": "stream.error",
                "code": code,
                "status": http_status,
                "message": message,
                "responseId": response_id,
                "segmentId": segment_id,
                "speechEpoch": speech_epoch,
                "totalSamples": total_samples,
            }
        )

    async def run_stream(req: StreamSynthesizeRequest, cancel_event: threading.Event) -> None:
        total_samples = 0
        ready_sent = False
        try:
            synth_engine.validate_stream_request(
                text=req.text,
                voice_id=req.voice,
                locale=req.locale,
                speed=req.speed,
            )
            async for pcm_chunk in synth_engine.synthesize_stream_async(
                text=req.text,
                voice_id=req.voice,
                locale=req.locale,
                speed=req.speed,
                cancel_event=cancel_event,
            ):
                if cancel_event.is_set() or disconnected:
                    break
                if not ready_sent:
                    if not await send_json(
                        {
                            "type": "stream.ready",
                            "version": 2,
                            "responseId": req.responseId,
                            "segmentId": req.segmentId,
                            "speechEpoch": req.speechEpoch,
                            "sampleRateHz": NATIVE_SAMPLE_RATE_HZ,
                            "channels": NATIVE_CHANNELS,
                            "format": "pcm16le",
                        }
                    ):
                        cancel_event.set()
                        return
                    ready_sent = True
                view = memoryview(pcm_chunk)
                for offset in range(0, len(view), MAX_BINARY_FRAME_BYTES):
                    if cancel_event.is_set() or disconnected:
                        break
                    frame = bytes(view[offset : offset + MAX_BINARY_FRAME_BYTES])
                    if not await send_bytes(frame):
                        cancel_event.set()
                        return
                    total_samples += len(frame) // 2

            if disconnected:
                return
            if cancel_event.is_set():
                await send_json(
                    {
                        "type": "stream.cancelled",
                        "responseId": req.responseId,
                        "segmentId": req.segmentId,
                        "speechEpoch": req.speechEpoch,
                        "totalSamples": total_samples,
                    }
                )
            else:
                await send_json(
                    {
                        "type": "stream.completed",
                        "responseId": req.responseId,
                        "segmentId": req.segmentId,
                        "speechEpoch": req.speechEpoch,
                        "totalSamples": total_samples,
                    }
                )
        except (VoiceUnknown, ValueError) as exc:
            await send_error(
                "invalid_request",
                422,
                str(exc),
                response_id=req.responseId,
                segment_id=req.segmentId,
                speech_epoch=req.speechEpoch,
                total_samples=total_samples,
            )
        except OverloadError as exc:
            await send_error(
                "overloaded",
                503,
                str(exc),
                response_id=req.responseId,
                segment_id=req.segmentId,
                speech_epoch=req.speechEpoch,
                total_samples=total_samples,
            )
        except ModelUnavailable:
            await send_error(
                "model_unavailable",
                503,
                "model_unavailable",
                response_id=req.responseId,
                segment_id=req.segmentId,
                speech_epoch=req.speechEpoch,
                total_samples=total_samples,
            )
        except asyncio.CancelledError:
            cancel_event.set()
            raise
        except Exception:
            await send_error(
                "internal_error",
                500,
                "synthesis_failed",
                response_id=req.responseId,
                segment_id=req.segmentId,
                speech_epoch=req.speechEpoch,
                total_samples=total_samples,
            )

    try:
        while True:
            raw = await websocket.receive_text()
            if len(raw.encode("utf-8")) > MAX_WS_CONTROL_BYTES:
                await send_error("invalid_request", 413, "control message too large")
                continue
            try:
                payload = json.loads(raw)
            except json.JSONDecodeError:
                await send_error("invalid_request", 422, "invalid json payload")
                continue
            if not isinstance(payload, dict) or not isinstance(payload.get("type"), str):
                await send_error("invalid_request", 422, "missing message type")
                continue

            msg_type = payload["type"]
            if msg_type == "cancel":
                try:
                    cancel_req = StreamCancelRequest(**payload)
                except ValidationError:
                    await send_error("invalid_request", 422, "invalid cancel payload")
                    continue
                if active_cancel is not None and active_request is not None:
                    response_matches = (
                        cancel_req.responseId is None
                        or cancel_req.responseId == active_request.responseId
                    )
                    epoch_matches = (
                        cancel_req.speechEpoch is None
                        or cancel_req.speechEpoch >= active_request.speechEpoch
                    )
                    if response_matches and epoch_matches:
                        active_cancel.set()
                continue

            if msg_type == "synthesize":
                try:
                    synth_req = StreamSynthesizeRequest(**payload)
                except ValidationError:
                    raw_epoch = payload.get("speechEpoch")
                    await send_error(
                        "invalid_request",
                        422,
                        "invalid synthesize payload",
                        response_id=str(payload.get("responseId") or ""),
                        segment_id=str(payload.get("segmentId") or ""),
                        speech_epoch=raw_epoch if isinstance(raw_epoch, int) else 0,
                    )
                    continue
                if active_task is not None and not active_task.done():
                    await send_error(
                        "conflict",
                        409,
                        "a synthesis stream is already active on this connection",
                        response_id=synth_req.responseId,
                        segment_id=synth_req.segmentId,
                        speech_epoch=synth_req.speechEpoch,
                    )
                    continue
                active_cancel = threading.Event()
                active_request = synth_req
                active_task = asyncio.create_task(run_stream(synth_req, active_cancel))
                continue

            await send_error("invalid_request", 422, "unsupported message type")
    except WebSocketDisconnect:
        disconnected = True
    finally:
        disconnected = True
        if active_cancel is not None:
            active_cancel.set()
        if active_task is not None:
            await asyncio.gather(active_task, return_exceptions=True)


@app.post("/internal/v1/execute", dependencies=[Depends(authenticate)])
def execute(request: ExecuteRequest) -> dict[str, object]:
    try:
        if configured_client() is not None:
            raise HTTPException(status_code=503, detail="execution unavailable")
    except HTTPException:
        raise
    except Exception:
        raise HTTPException(status_code=503, detail="execution unavailable") from None
    return run_synthesis(request)


def run_synthesis(request: ExecuteRequest) -> dict[str, object]:
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


@app.post("/internal/cloud/v1/voice-synthesis/execute", dependencies=[Depends(authenticate)])
async def cloud_execute(request: Request):
    if request.headers.get("content-type", "").lower() != "application/json" or "content-encoding" in request.headers:
        raise HTTPException(status_code=415, detail="unsupported media type")
    body = bytearray()
    try:
        async for chunk in request.stream():
            if len(body) + len(chunk) > 100000:
                raise HTTPException(status_code=413, detail="request too large")
            body.extend(chunk)
        result = await run_in_threadpool(execute_cloud, bytes(body), configured_client(), run_synthesis)
        if result is None:
            raise HTTPException(status_code=503, detail="execution unavailable")
        return result
    except HTTPException:
        raise
    except Exception:
        raise HTTPException(status_code=503, detail="execution unavailable") from None
    finally:
        body[:] = b"\0" * len(body)


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
