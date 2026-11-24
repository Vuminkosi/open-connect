"""Persistent v2 streaming STT: Cloud Muse ENDPOINTING primary, local Whisper fallback."""

from __future__ import annotations

import asyncio
import json
import logging
import uuid
from typing import Any

import websockets
from fastapi import WebSocket, WebSocketDisconnect

from openconnect_transcription import cloud, realtime_local, streaming_local
from openconnect_transcription.config import Settings

logger = logging.getLogger("openconnect_transcription_realtime_v2")

_PARTIAL_TYPES = frozenset({"speechPartial", "transcript.partial", "partial", "transcript", "interim"})
_FINAL_TYPES = frozenset({"speechComplete", "transcript.final", "final"})


def cloud_streaming_available(settings: Settings) -> bool:
    """Return True when Cloud Muse realtime is configured and permitted by privacy policy."""
    return (
        settings.cloud_url is not None
        and settings.cloud_api_key is not None
        and settings.model_id is not None
        and settings.model_id != ""
        and not cloud.privacy_zone_strict()
    )


def local_streaming_available(settings: Settings) -> bool:
    """Return True when verified local Whisper streaming is available."""
    return (
        not settings.fake_engine
        and settings.backend == "local-model"
        and realtime_local.load_local_model(settings) is not None
    )


async def transcribe_v2(websocket: WebSocket, settings: Settings) -> None:
    """Serve persistent v2 streaming STT: Cloud Muse primary, local Whisper fallback."""
    await websocket.accept()

    if not cloud_streaming_available(settings):
        await streaming_local.transcribe_stream(websocket, settings, accept=False)
        return

    assert settings.cloud_api_key is not None and settings.model_id is not None
    handshake = json.dumps(
        {
            "authorization": {"accessToken": f"Bearer {settings.cloud_api_key}"},
            "audioEncoding": "PCM_24KHZ",
            "model": settings.model_id,
            "mode": settings.transcription_mode,
        }
    )

    try:
        upstream_cm = websockets.connect(settings.transcription_realtime_url, open_timeout=10)
        upstream = await upstream_cm.__aenter__()
    except Exception as exc:
        logger.info("Cloud Muse connect failed outcome=%s; checking local fallback", type(exc).__name__)
        if local_streaming_available(settings):
            await streaming_local.transcribe_stream(websocket, settings, accept=False)
            return
        await websocket.close(code=1013, reason="provider_unavailable")
        return

    try:
        try:
            await upstream.send(handshake)
            await asyncio.wait_for(upstream.recv(), timeout=10)
        except Exception as exc:
            logger.info(
                "Cloud Muse handshake failed outcome=%s; checking local fallback",
                type(exc).__name__,
            )
            await upstream_cm.__aexit__(None, None, None)
            if local_streaming_available(settings):
                await streaming_local.transcribe_stream(websocket, settings, accept=False)
                return
            await websocket.close(code=1013, reason="provider_unavailable")
            return

        await _bridge_muse_session(websocket, upstream, settings)
    finally:
        try:
            await upstream_cm.__aexit__(None, None, None)
        except Exception:
            pass


async def _bridge_muse_session(
    websocket: WebSocket,
    upstream: Any,
    settings: Settings,
) -> None:
    """Bridge persistent multi-utterance v2 control/PCM messages with Cloud Muse."""
    reconciler = streaming_local.TranscriptReconciler()
    capture_id = f"cap-{uuid.uuid4().hex[:12]}"
    speech_epoch = 0
    utterance_bytes = 0
    utterance_cancelled = False
    send_lock = asyncio.Lock()

    duration_limit = min(settings.max_duration_ms, streaming_local.MAX_UTTERANCE_MS)
    utterance_limit = min(
        settings.max_audio_bytes,
        streaming_local.MAX_UTTERANCE_BYTES,
        duration_limit * streaming_local.BYTES_PER_SECOND // 1000,
    )
    if utterance_limit < streaming_local.MIN_AUDIO_BYTES:
        utterance_limit = streaming_local.MAX_UTTERANCE_BYTES

    await websocket.send_json(
        {
            "type": "sessionReady",
            "version": 2,
            "provider": "cloud-muse",
            "mode": settings.transcription_mode,
            "supportsPartials": True,
            "sampleRateHz": streaming_local.SAMPLE_RATE_HZ,
            "channels": streaming_local.CHANNELS,
        }
    )

    async def _send_client(payload: dict[str, Any]) -> None:
        async with send_lock:
            await websocket.send_json(payload)

    async def client_to_provider() -> None:
        nonlocal capture_id, speech_epoch, utterance_bytes, utterance_cancelled
        while True:
            message = await websocket.receive()
            if message.get("type") == "websocket.disconnect":
                await upstream.close()
                return

            payload = message.get("bytes")
            if payload is not None:
                if (
                    not payload
                    or len(payload) % 2 != 0
                    or len(payload) > streaming_local.MAX_FRAME_BYTES
                    or utterance_bytes + len(payload) > utterance_limit
                ):
                    raise ValueError("invalid_pcm_stream")
                utterance_bytes += len(payload)
                utterance_cancelled = False
                await upstream.send(payload)
                continue

            raw_text = message.get("text")
            if raw_text is None:
                continue
            try:
                control = json.loads(raw_text)
            except (ValueError, TypeError):
                raise ValueError("invalid_control") from None

            if not isinstance(control, dict) or not isinstance(control.get("type"), str):
                raise ValueError("invalid_control")

            msg_type = control["type"]
            if msg_type == "startUtterance":
                cid = streaming_local._parse_capture_id(control.get("captureId"), required=True)
                epoch = streaming_local._parse_speech_epoch(control.get("speechEpoch"), default=0)
                if not cid or epoch is None:
                    raise ValueError("invalid_control")
                capture_id = cid
                speech_epoch = epoch
                utterance_bytes = 0
                utterance_cancelled = False
                reconciler.reset()
                continue

            if msg_type == "cancelUtterance":
                utterance_cancelled = True
                utterance_bytes = 0
                reconciler.reset()
                continue

            if msg_type == "endUtterance":
                req_cid = streaming_local._parse_capture_id(
                    control.get("captureId"), required=False
                )
                req_epoch = streaming_local._parse_speech_epoch(
                    control.get("speechEpoch"), default=None
                )
                if req_cid == "" or ("speechEpoch" in control and req_epoch is None):
                    raise ValueError("invalid_control")
                if req_cid is not None:
                    capture_id = req_cid
                if req_epoch is not None:
                    speech_epoch = req_epoch
                await upstream.send(json.dumps({"type": "endUtterance"}))
                continue

            if msg_type == "closeSession":
                await upstream.close()
                await websocket.close(code=1000)
                return

            raise ValueError("invalid_control")

    async def provider_to_client() -> None:
        nonlocal utterance_bytes, utterance_cancelled
        async for raw_msg in upstream:
            if isinstance(raw_msg, bytes):
                continue
            try:
                event = json.loads(raw_msg)
            except (ValueError, TypeError):
                continue
            if not isinstance(event, dict):
                continue

            evt_type = str(event.get("type") or "")
            raw_transcript = event.get("text")
            if not isinstance(raw_transcript, str):
                raw_transcript = event.get("transcript")
            if not isinstance(raw_transcript, str):
                continue

            if utterance_cancelled:
                continue

            is_final = evt_type in _FINAL_TYPES or bool(event.get("isFinal"))
            if is_final:
                final = reconciler.finalize(raw_transcript)
                await _send_client(
                    {
                        "type": "transcript.final",
                        "captureId": capture_id,
                        "speechEpoch": speech_epoch,
                        "revision": final.revision,
                        "text": final.text,
                        "isEmpty": final.is_empty,
                        "authoritative": True,
                    }
                )
                utterance_bytes = 0
                reconciler.reset()
            elif evt_type in _PARTIAL_TYPES or not is_final:
                hyp = reconciler.reconcile_partial(raw_transcript)
                if hyp is not None:
                    await _send_client(
                        {
                            "type": "transcript.partial",
                            "captureId": capture_id,
                            "speechEpoch": speech_epoch,
                            "revision": hyp.revision,
                            "text": hyp.text,
                            "stableText": hyp.stable_text,
                            "unstableText": hyp.unstable_text,
                            "authoritative": False,
                        }
                    )

    try:
        sender = asyncio.create_task(client_to_provider())
        receiver = asyncio.create_task(provider_to_client())
        done, pending = await asyncio.wait({sender, receiver}, return_when=asyncio.FIRST_COMPLETED)
        for task in pending:
            task.cancel()
        for task in done:
            task.result()
    except WebSocketDisconnect:
        return
    except ValueError as exc:
        await websocket.close(code=1003, reason=str(exc))
    except Exception as exc:
        logger.info("Realtime v2 Muse session failed outcome=%s", type(exc).__name__)
        try:
            await websocket.close(code=1013, reason="provider_unavailable")
        except Exception:
            pass
