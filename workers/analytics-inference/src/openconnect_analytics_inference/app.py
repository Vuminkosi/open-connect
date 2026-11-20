"""Private, stateless analytics endpoint for a trusted OpenConnect host."""

from __future__ import annotations

import asyncio
import os
import re
from dataclasses import asdict
from datetime import datetime, timedelta, timezone
from typing import Literal

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, StrictFloat, ValidationError

from openconnect_analytics_inference.statistics import MAX_SAMPLES, analyze
from openconnect_worker_common.contracts.capability import (
    CapabilityDescriptor,
    CapabilityKind,
    CapabilityState,
)
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.signing import key_from_environment, verify_bytes

MAX_BODY_BYTES = 256 * 1024
MAX_REQUEST_LIFETIME = timedelta(minutes=5)
app = FastAPI(title="OpenConnect Analytics Worker", docs_url=None, redoc_url=None, openapi_url=None)


class AnalyticsPayload(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    feature: str = Field(min_length=1, max_length=128)
    unit: str = Field(min_length=1, max_length=64)
    lineage: str = Field(min_length=1, max_length=256)
    values: list[StrictFloat | None] = Field(max_length=MAX_SAMPLES)
    threshold: StrictFloat = Field(default=3.5, gt=0, le=100)


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["analytics.analyze"]
    tenantId: str = Field(min_length=1, max_length=128)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: AnalyticsPayload


def _configured() -> bool:
    key = key_from_environment()
    return bool(os.environ.get("OPENCONNECT_WORKER_TOKEN")) and bool(key and len(key) >= 32)


def _error(code: str, status: int) -> JSONResponse:
    return JSONResponse({"error": {"code": code}}, status_code=status)


def _authenticated(request: Request) -> bool:
    try:
        return verify_bearer(
            request.headers.get("authorization"), os.environ.get("OPENCONNECT_WORKER_TOKEN")
        )
    except TypeError:
        return False


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> JSONResponse:
    return JSONResponse({"ready": _configured()}, status_code=200 if _configured() else 503)


@app.get("/internal/v1/capabilities")
def capabilities(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authenticated(request):
        return _error("unauthorized", 401)
    descriptor = CapabilityDescriptor(
        id="analytics.analyze", kind=CapabilityKind.TOOL, state=CapabilityState.READY,
        version="1.0.0", localOnly=True,
    )
    return JSONResponse({"capabilities": [descriptor.model_dump(mode="json")]})


@app.post("/internal/v1/execute")
async def execute(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authenticated(request):
        return _error("unauthorized", 401)
    body = bytearray()
    try:
        async with asyncio.timeout(10):
            async for chunk in request.stream():
                if len(body) + len(chunk) > MAX_BODY_BYTES:
                    return _error("invalid_request", 413)
                body.extend(chunk)
    except TimeoutError:
        return _error("invalid_request", 408)
    key = key_from_environment()
    signature = request.headers.get("x-openconnect-signature", "")
    if not key or not re.fullmatch(r"[0-9a-f]{64}", signature) or not verify_bytes(
        bytes(body), signature, key
    ):
        return _error("unauthorized", 401)
    try:
        command = ExecuteRequest.model_validate_json(body)
        now = datetime.now(timezone.utc)
        if (command.issuedAt > now or command.expiresAt <= now
                or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > MAX_REQUEST_LIFETIME):
            return _error("invalid_request", 422)
        result = analyze(command.payload.values, threshold=command.payload.threshold)
    except (ValidationError, ValueError, OverflowError):
        return _error("invalid_request", 422)
    return JSONResponse({
        "schemaVersion": "openconnect.v1", "taskId": command.taskId,
        "kind": command.kind, "result": {
            "feature": command.payload.feature, "unit": command.payload.unit,
            "lineage": command.payload.lineage, **asdict(result),
        },
    })
