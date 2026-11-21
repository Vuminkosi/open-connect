"""Local-only transcription boundary for inspecting generated audio."""

from __future__ import annotations

import base64
import hashlib
import json
from collections.abc import Callable

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import ValidationError
from starlette.concurrency import run_in_threadpool

from openconnect_transcription import engine_mode
from openconnect_transcription.config import Settings
from openconnect_transcription.engine import local_whisper
from openconnect_transcription.engine.audio import AudioValidationError, validate_audio
from openconnect_transcription.schemas import ExecuteRequest


def create_audio_inspection_router(settings: Callable[[], Settings], authenticate: Callable) -> APIRouter:
    router = APIRouter()

    @router.post("/internal/v1/audio-inspection/transcribe", dependencies=[Depends(authenticate)])
    async def inspect(http: Request) -> JSONResponse:
        if http.headers.get("content-type", "").split(";", 1)[0].strip().lower() != "application/json" or "content-encoding" in http.headers:
            raise HTTPException(status_code=415, detail="invalid_request")
        limit = 7_002_048
        length = http.headers.get("content-length")
        if length is not None and (not length.isascii() or not length.isdigit() or int(length) > limit):
            raise HTTPException(status_code=413, detail="invalid_request")
        body = bytearray()
        try:
            async for chunk in http.stream():
                if len(body) + len(chunk) > limit:
                    raise HTTPException(status_code=413, detail="invalid_request")
                body.extend(chunk)

            def unique(pairs):
                result = {}
                for key, value in pairs:
                    if key in result:
                        raise ValueError("duplicate JSON property")
                    result[key] = value
                return result

            try:
                value = json.loads(body, object_pairs_hook=unique)
                request = ExecuteRequest.model_validate(value)
            except (ValueError, ValidationError, UnicodeDecodeError) as exc:
                raise HTTPException(status_code=400, detail="invalid_request") from exc
            result = await run_in_threadpool(transcribe, request)
            return JSONResponse(result, headers={"Cache-Control": "no-store"})
        finally:
            body[:] = b"\x00" * len(body)

    def transcribe(request: ExecuteRequest) -> dict[str, object]:
        cfg = settings()
        if (cfg.fake_engine or cfg.backend != "local-model" or cfg.cloud_url is not None or
                request.idempotencyKey is not None or request.input.partialsRequested):
            raise HTTPException(status_code=503, detail="capability_unavailable")
        info, status, code = engine_mode.rejection(False, cfg.model_device)
        if code is not None or info is None:
            raise HTTPException(status_code=status or 503, detail=code or "capability_unavailable")
        model = local_whisper.load_model(cfg, info.effective)
        if model is None:
            raise HTTPException(status_code=503, detail="model_unavailable")
        try:
            audio = validate_audio(audio_base64=request.input.audioBase64, mime_type=request.input.mimeType,
                sample_rate_hz=request.input.sampleRateHz, channels=request.input.channels,
                bits_per_sample=request.input.bitsPerSample, duration_ms=request.input.durationMs,
                max_audio_bytes=cfg.max_audio_bytes, max_duration_ms=cfg.max_duration_ms)
        except AudioValidationError as exc:
            raise HTTPException(status_code=400, detail="invalid_request") from exc
        try:
            result = local_whisper.transcribe(model, audio, request.input.locale)
        except Exception as exc:
            raise HTTPException(status_code=503, detail="model_unavailable") from exc
        encoded = base64.b64decode(request.input.audioBase64, validate=True)
        return {"schemaVersion": "openconnect.audio-inspection.transcript/v1", "requestId": str(request.requestId),
                "modelId": cfg.model_id, "modelRevision": cfg.model_revision,
                "audioSha256": hashlib.sha256(encoded).hexdigest(), "transcript": result.transcript,
                "locale": result.locale, "durationMs": result.duration_ms}

    return router
