"""Opt-in private Cloud endpoint; legacy execution is disabled in Cloud mode."""

import asyncio
import os
import secrets
from functools import lru_cache
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from .cloud_client import CloudLiveUIClient
from .cloud_execution import execute_claimed

router = APIRouter()


def cloud_enabled() -> bool:
    return os.environ.get("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "false").lower() == "true"


def cloud_mode_blocks_legacy() -> bool:
    return os.environ.get("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "false").lower() != "false"


@lru_cache(maxsize=1)
def configured_client() -> CloudLiveUIClient | None:
    if not cloud_enabled():
        return None
    values = [
        os.environ.get(name)
        for name in (
            "OPENCONNECT_CLOUD_AUTHORITY_ORIGIN",
            "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE",
            "OPENCONNECT_CLOUD_WORKER_CERT_FILE",
            "OPENCONNECT_CLOUD_WORKER_KEY_FILE",
        )
    ]
    if not all(values):
        return None
    origin, ca, cert, key = values
    assert origin is not None and ca is not None and cert is not None and key is not None
    try:
        return CloudLiveUIClient(origin, Path(ca), Path(cert), Path(key))
    except (OSError, ValueError):
        return None


def authorized(request: Request) -> bool:
    settings = getattr(request.app.state, "settings", None)
    token = getattr(settings, "bearer_token", "")
    values = request.headers.getlist("authorization")
    if not token or len(values) != 1 or not values[0].startswith("Bearer "):
        return False
    try:
        return secrets.compare_digest(values[0][7:], token)
    except TypeError:
        return False


def unavailable() -> JSONResponse:
    return JSONResponse(status_code=503, content={"status": "unavailable"}, headers={"Cache-Control": "no-store"})


@router.get("/internal/cloud/v1/liveui/ready")
async def ready(request: Request) -> JSONResponse:
    if not authorized(request):
        return JSONResponse(status_code=401, content={"status": "unauthorized"})
    client = configured_client()
    if (
        not cloud_enabled()
        or not getattr(request.app.state, "ready", False)
        or getattr(request.app.state, "engine", None) is None
        or client is None
    ):
        return unavailable()
    deployment = await run_in_threadpool(client.ready_deployment)
    return (
        JSONResponse({"status": "ready", "deploymentId": deployment}, headers={"Cache-Control": "no-store"})
        if deployment
        else unavailable()
    )


@router.post("/internal/cloud/v1/liveui/execute")
async def execute(request: Request) -> JSONResponse:
    if not authorized(request):
        return JSONResponse(status_code=401, content={"status": "unauthorized"})
    if request.headers.get("content-type", "").lower() != "application/json" or "content-encoding" in request.headers:
        return JSONResponse(status_code=415, content={"status": "invalid_request"})
    client = configured_client()
    if (
        not cloud_enabled()
        or not getattr(request.app.state, "ready", False)
        or getattr(request.app.state, "engine", None) is None
        or client is None
    ):
        return unavailable()
    body = bytearray()
    acquired = False
    future = None
    try:
        async for chunk in request.stream():
            if len(body) + len(chunk) > 180000:
                return JSONResponse(status_code=413, content={"status": "invalid_request"})
            body.extend(chunk)
        try:
            await asyncio.wait_for(request.app.state.slots.acquire(), timeout=0.1)
            acquired = True
        except TimeoutError:
            return unavailable()
        future = asyncio.get_running_loop().run_in_executor(
            request.app.state.executor,
            execute_claimed,
            bytes(body),
            request.app.state.engine,
            request.app.state.settings,
            client,
            os.environ.get("OPENCONNECT_CLOUD_LIVEUI_METERED_ENABLED", "false").lower() == "true",
        )
        result = await asyncio.wait_for(
            asyncio.shield(future), timeout=request.app.state.settings.request_timeout_seconds
        )
        return JSONResponse(result, headers={"Cache-Control": "no-store"}) if result is not None else unavailable()
    except TimeoutError:
        if future is not None and not future.done():
            acquired = False
            future.add_done_callback(lambda _: request.app.state.slots.release())
        return unavailable()
    except asyncio.CancelledError:
        if future is not None and not future.done():
            acquired = False
            future.add_done_callback(lambda _: request.app.state.slots.release())
        raise
    except Exception:
        return unavailable()
    finally:
        if acquired:
            request.app.state.slots.release()
        body[:] = b"\0" * len(body)
