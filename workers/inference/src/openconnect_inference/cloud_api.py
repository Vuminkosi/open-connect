"""Opt-in Cloud inference route. No production model runtime is supplied here."""
from __future__ import annotations

import hmac
import os
from functools import lru_cache
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from openconnect_inference.cloud_consumption import CloudConsumptionClient
from openconnect_inference.cloud_execution import execute_claimed

router = APIRouter()


def cloud_enabled() -> bool:
    return os.environ.get("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "false").lower() == "true"


def cloud_mode_blocks_legacy() -> bool:
    return os.environ.get("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "false").lower() != "false"


@lru_cache(maxsize=1)
def configured_client() -> CloudConsumptionClient | None:
    if not cloud_enabled():
        return None
    names = ("OPENCONNECT_CLOUD_AUTHORITY_ORIGIN", "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE",
             "OPENCONNECT_CLOUD_WORKER_CERT_FILE", "OPENCONNECT_CLOUD_WORKER_KEY_FILE")
    values = [os.environ.get(name) for name in names]
    if not all(values):
        return None
    origin, ca, cert, key = values
    assert origin is not None and ca is not None and cert is not None and key is not None
    try:
        return CloudConsumptionClient(origin, Path(ca), Path(cert), Path(key))
    except (ValueError, OSError):
        return None


def _authorized(request: Request) -> bool:
    token = os.environ.get("OPENCONNECT_WORKER_TOKEN", "")
    values = request.headers.getlist("authorization")
    if not token or len(values) != 1 or not values[0].startswith("Bearer "):
        return False
    try:
        return hmac.compare_digest(values[0].removeprefix("Bearer "), token)
    except TypeError:
        return False


def _unavailable() -> JSONResponse:
    return JSONResponse(status_code=503, content={"status": "unavailable"}, headers={"Cache-Control": "no-store"})


@router.get("/internal/cloud/v1/inference/ready")
async def ready(request: Request) -> JSONResponse:
    if not _authorized(request):
        return JSONResponse(status_code=401, content={"status": "unauthorized"})
    runtime = getattr(request.app.state, "cloud_inference_runtime", None)
    client = configured_client()
    if not cloud_enabled() or runtime is None or client is None:
        return _unavailable()
    deployment = await run_in_threadpool(client.ready_deployment)
    if deployment is None:
        return _unavailable()
    return JSONResponse({"status": "ready", "deploymentId": deployment}, headers={"Cache-Control": "no-store"})


@router.post("/internal/cloud/v1/inference/execute")
async def execute(request: Request) -> JSONResponse:
    if not _authorized(request):
        return JSONResponse(status_code=401, content={"status": "unauthorized"})
    if request.headers.get("content-type", "").lower() != "application/json" or "content-encoding" in request.headers:
        return JSONResponse(status_code=415, content={"status": "invalid_request"})
    runtime = getattr(request.app.state, "cloud_inference_runtime", None)
    client = configured_client()
    if not cloud_enabled() or runtime is None or client is None:
        return _unavailable()
    body = bytearray()
    try:
        async for chunk in request.stream():
            if len(body) + len(chunk) > 180000:
                return JSONResponse(status_code=413, content={"status": "invalid_request"})
            body.extend(chunk)
        result = await run_in_threadpool(execute_claimed, bytes(body), runtime, client)
        return JSONResponse(result, headers={"Cache-Control": "no-store"}) if result is not None else _unavailable()
    except Exception:
        return _unavailable()
    finally:
        body[:] = b"\0" * len(body)
