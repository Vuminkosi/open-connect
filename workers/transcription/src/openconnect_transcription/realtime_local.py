"""Final-only local PCM transcription; never forwards audio to another service."""

from __future__ import annotations

import asyncio
import json
import threading

from fastapi import WebSocket, WebSocketDisconnect

from openconnect_transcription import engine_mode
from openconnect_transcription.config import Settings
from openconnect_transcription.engine import local_whisper
from openconnect_transcription.engine.audio import ValidatedAudio

_slots = threading.BoundedSemaphore(2)
_SAMPLE_RATE = 24_000
_BYTES_PER_SECOND = _SAMPLE_RATE * 2
_MAX_FRAME = 96 * 1024
_MAX_CAPTURE_BYTES = 32 * 1024 * 1024
_MAX_CAPTURE_MS = 120_000


def load_local_model(settings: Settings):
    """Load only a verified local model; this does not reserve capture capacity."""
    if settings.backend != "local-model" or settings.fake_engine:
        return None
    duration_limit = min(settings.max_duration_ms, _MAX_CAPTURE_MS)
    limit = min(settings.max_audio_bytes, _MAX_CAPTURE_BYTES,
                duration_limit * _BYTES_PER_SECOND // 1000)
    if limit < _BYTES_PER_SECOND // 1000:
        return None
    device = engine_mode.device_info(settings.model_device)
    return None if device.gpu_required_missing else local_whisper.load_model(settings, device.effective)


async def _run_blocking(function, *args):
    # A cancelled request must keep its slot until the model thread stops.
    task = asyncio.create_task(asyncio.to_thread(function, *args))
    try:
        return await asyncio.shield(task)
    except asyncio.CancelledError:
        try:
            await task
        finally:
            raise


async def transcribe(websocket: WebSocket, settings: Settings) -> None:
    """Accept one bounded mono PCM16 capture and emit one authoritative final."""
    await websocket.accept()
    if settings.backend != "local-model" or settings.fake_engine or not _slots.acquire(False):
        await websocket.close(code=1013, reason="model_unavailable")
        return
    try:
        model = await _run_blocking(load_local_model, settings)
        if model is None:
            await websocket.close(code=1013, reason="model_unavailable")
            return
        await websocket.send_json({"type": "sessionReady"})
        duration_limit = min(settings.max_duration_ms, _MAX_CAPTURE_MS)
        limit = min(settings.max_audio_bytes, _MAX_CAPTURE_BYTES,
                    duration_limit * _BYTES_PER_SECOND // 1000)
        if limit < _BYTES_PER_SECOND // 1000:
            await websocket.close(code=1013, reason="model_unavailable")
            return
        audio = bytearray()
        deadline = asyncio.get_running_loop().time() + duration_limit / 1000 + 10
        while True:
            remaining = deadline - asyncio.get_running_loop().time()
            if remaining <= 0:
                raise asyncio.TimeoutError
            message = await asyncio.wait_for(websocket.receive(), timeout=remaining)
            if message["type"] == "websocket.disconnect":
                return
            payload = message.get("bytes")
            if payload is not None:
                if (not payload or len(payload) % 2 or len(payload) > _MAX_FRAME
                        or len(audio) + len(payload) > limit):
                    await websocket.close(code=1003, reason="invalid_pcm_stream")
                    return
                audio.extend(payload)
                continue
            try:
                control = json.loads(message.get("text") or "")
            except (ValueError, TypeError):
                control = None
            if control != {"type": "endStream"}:
                await websocket.close(code=1003, reason="invalid_control")
                return
            if len(audio) < _BYTES_PER_SECOND // 1000:
                await websocket.close(code=1003, reason="empty_audio")
                return
            duration = len(audio) * 1000 // _BYTES_PER_SECOND
            validated = ValidatedAudio(bytes(audio), "audio/pcm", _SAMPLE_RATE, 1, 16, duration)
            result = await _run_blocking(local_whisper.transcribe, model, validated, settings.default_locale)
            if len(result.transcript.encode("utf-8")) > _MAX_FRAME // 2:
                await websocket.close(code=1013, reason="transcription_unavailable")
                return
            await websocket.send_json({"type": "speechComplete", "text": result.transcript})
            await websocket.close(code=1000)
            return
    except WebSocketDisconnect:
        return
    except asyncio.TimeoutError:
        await websocket.close(code=1008, reason="capture_timeout")
    except Exception:
        await websocket.close(code=1013, reason="transcription_unavailable")
    finally:
        _slots.release()
