"""SwiftAid-compatible Meta realtime transcription proxy."""

from __future__ import annotations

import asyncio
import json
import logging
from typing import Any

import websockets
from fastapi import WebSocket, WebSocketDisconnect

from openconnect_transcription.config import Settings
from openconnect_transcription import cloud

logger = logging.getLogger("openconnect_transcription_realtime")

MAX_PCM_BYTES = 32 * 1024 * 1024


async def proxy(websocket: WebSocket, settings: Settings) -> None:
    await websocket.accept()
    if settings.cloud_url is None or settings.cloud_api_key is None or settings.model_id is None:
        await websocket.close(code=1013, reason="model_unavailable")
        return
    if cloud.privacy_zone_strict():
        await websocket.close(code=1008, reason="privacy_blocked")
        return

    handshake = json.dumps(
        {
            "authorization": {"accessToken": f"Bearer {settings.cloud_api_key}"},
            "audioEncoding": "PCM_24KHZ",
            "model": settings.model_id,
            "mode": settings.transcription_mode,
        }
    )
    try:
        async with websockets.connect(settings.transcription_realtime_url, open_timeout=10) as upstream:
            await upstream.send(handshake)
            acknowledgement = await asyncio.wait_for(upstream.recv(), timeout=10)
            await websocket.send_text(acknowledgement if isinstance(acknowledgement, str) else acknowledgement.decode())

            async def client_to_provider() -> None:
                nonlocal_bytes = 0
                while True:
                    message = await websocket.receive()
                    if message.get("type") == "websocket.disconnect":
                        await upstream.close()
                        return
                    if message.get("bytes") is not None:
                        payload = message["bytes"]
                        nonlocal_bytes += len(payload)
                        if nonlocal_bytes > MAX_PCM_BYTES or len(payload) % 2 != 0:
                            raise ValueError("invalid_pcm_stream")
                        await upstream.send(payload)
                    elif message.get("text") is not None:
                        await upstream.send(message["text"])

            async def provider_to_client() -> None:
                async for message in upstream:
                    if isinstance(message, bytes):
                        await websocket.send_bytes(message)
                    else:
                        await websocket.send_text(message)

            sender = asyncio.create_task(client_to_provider())
            receiver = asyncio.create_task(provider_to_client())
            done, pending = await asyncio.wait({sender, receiver}, return_when=asyncio.FIRST_COMPLETED)
            for task in pending:
                task.cancel()
            for task in done:
                task.result()
    except WebSocketDisconnect:
        return
    except asyncio.TimeoutError:
        await websocket.close(code=1013, reason="provider_timeout")
    except ValueError as exc:
        await websocket.close(code=1003, reason=str(exc))
    except Exception as exc:
        logger.info("Realtime transcription proxy failed outcome=%s", type(exc).__name__)
        try:
            await websocket.close(code=1013, reason="provider_unavailable")
        except Exception:
            pass
