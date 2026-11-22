"""Private, stateless analytics endpoint for a trusted OpenConnect host."""

from __future__ import annotations

import asyncio
import json
import os
import re
import threading
import time
from dataclasses import asdict
from datetime import datetime, timedelta, timezone
from functools import lru_cache
from pathlib import Path
from typing import Literal

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, StrictBool, StrictFloat, StrictInt, ValidationError

from openconnect_analytics_inference.forecast import MAX_HORIZON, MAX_INTERVAL_SECONDS, MAX_SEASON, forecast
from openconnect_analytics_inference.statistics import MAX_SAMPLES, analyze
from openconnect_analytics_inference.trend import trend
from openconnect_worker_common.contracts.capability import (
    CapabilityDescriptor,
    CapabilityKind,
    CapabilityState,
)
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.signing import key_from_environment, verify_bytes

MAX_BODY_BYTES = 256 * 1024
MAX_REQUEST_LIFETIME = timedelta(minutes=5)
_slot = threading.BoundedSemaphore(1)
app = FastAPI(title="OpenConnect Analytics Worker", docs_url=None, redoc_url=None, openapi_url=None)


class ForecastSettings(BaseModel):
    model_config = ConfigDict(extra="forbid")

    timestamps: list[AwareDatetime] = Field(max_length=MAX_SAMPLES, repr=False)
    intervalSeconds: StrictInt = Field(ge=1, le=MAX_INTERVAL_SECONDS)
    seasonalPeriod: StrictInt = Field(ge=1, le=MAX_SEASON)
    horizon: StrictInt = Field(ge=1, le=MAX_HORIZON)


class AnalyticsPayload(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)

    feature: str = Field(min_length=1, max_length=128, repr=False)
    unit: str = Field(min_length=1, max_length=64, repr=False)
    lineage: str = Field(min_length=1, max_length=256, repr=False)
    values: list[StrictFloat | None] = Field(max_length=MAX_SAMPLES, repr=False)
    threshold: StrictFloat = Field(default=3.5, gt=0, le=100)
    forecast: ForecastSettings | None = None
    trend: StrictBool = False


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128, repr=False)
    kind: Literal["analytics.analyze"]
    tenantId: str = Field(min_length=1, max_length=128, repr=False)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: AnalyticsPayload


class _Busy(Exception):
    pass


def _calculate(command: ExecuteRequest):
    if not _slot.acquire(blocking=False):
        raise _Busy()
    try:
        if datetime.now(timezone.utc) >= command.expiresAt:
            raise ValueError("expired_request")
        payload = command.payload
        result = analyze(payload.values, threshold=payload.threshold)
        settings = payload.forecast
        forecast_result = (forecast(payload.values, settings.timestamps,
                           interval_seconds=settings.intervalSeconds, seasonal_period=settings.seasonalPeriod,
                           horizon=settings.horizon) if settings is not None else None)
        trend_result = trend(payload.values) if payload.trend else None
        return result, forecast_result, trend_result
    finally:
        _slot.release()


def _calculate_measured(command: ExecuteRequest):
    """Measure worker-side elapsed calculation time, excluding admission and transport."""
    started = time.perf_counter_ns()
    result = _calculate(command)
    elapsed = time.perf_counter_ns() - started
    if elapsed < 0 or elapsed > 120_000_000_000:
        raise ValueError("invalid_compute_duration")
    return result, max(1, (elapsed + 999_999) // 1_000_000)


def _configured() -> bool:
    key = key_from_environment()
    return bool(os.environ.get("OPENCONNECT_WORKER_TOKEN")) and bool(key and len(key) >= 32)


def _error(code: str, status: int) -> JSONResponse:
    return JSONResponse({"error": {"code": code}}, status_code=status)


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result = {}
    for name, value in pairs:
        if name in result:
            raise ValueError("invalid_json")
        result[name] = value
    return result


def _authenticated(request: Request) -> bool:
    try:
        return verify_bearer(
            request.headers.get("authorization"), os.environ.get("OPENCONNECT_WORKER_TOKEN")
        )
    except TypeError:
        return False


@lru_cache(maxsize=1)
def _cloud_consumer():
    from openconnect_analytics_inference.cloud_consumption import AnalyticsClaimConsumer

    if os.environ.get("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "false").lower() != "true":
        return None
    names = ("OPENCONNECT_CLOUD_AUTHORITY_ORIGIN", "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE",
             "OPENCONNECT_CLOUD_WORKER_CERT_FILE", "OPENCONNECT_CLOUD_WORKER_KEY_FILE")
    origin, ca, cert, key = (os.environ.get(name) for name in names)
    if not all((origin, ca, cert, key)):
        raise ValueError("incomplete cloud configuration")
    return AnalyticsClaimConsumer(origin, Path(ca), Path(cert), Path(key))


def _ready() -> bool:
    if not _configured():
        return False
    enabled = os.environ.get("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "false").lower()
    if enabled == "false":
        return True
    if enabled != "true":
        return False
    try:
        return _cloud_consumer() is not None
    except (OSError, ValueError, TypeError):
        return False


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> JSONResponse:
    available = _ready()
    return JSONResponse({"ready": available}, status_code=200 if available else 503)


@app.get("/internal/v1/capabilities")
def capabilities(request: Request) -> JSONResponse:
    if not _ready():
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
    if os.environ.get("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "false").lower() != "false":
        return _error("capability_unavailable", 503)
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
        parsed = json.loads(body, object_pairs_hook=_unique_object,
                            parse_constant=lambda _: (_ for _ in ()).throw(ValueError("invalid_json")))
        command = ExecuteRequest.model_validate(parsed)
        now = datetime.now(timezone.utc)
        if (command.issuedAt > now or command.expiresAt <= now
                or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > MAX_REQUEST_LIFETIME):
            return _error("invalid_request", 422)
    except (ValidationError, ValueError, OverflowError, RecursionError):
        return _error("invalid_request", 422)
    return await _execute_command(command)


async def _execute_command(command: ExecuteRequest, *, cloud: bool = False) -> JSONResponse:
    try:
        remaining = (command.expiresAt - datetime.now(timezone.utc)).total_seconds()
        async with asyncio.timeout(remaining):
            if cloud:
                (result, forecast_result, trend_result), compute_ms = await run_in_threadpool(_calculate_measured, command)
            else:
                result, forecast_result, trend_result = await run_in_threadpool(_calculate, command)
        if datetime.now(timezone.utc) >= command.expiresAt:
            return _error("invalid_request", 422)
    except (ValueError, OverflowError, RecursionError, TimeoutError):
        return _error("invalid_request", 422)
    except _Busy:
        return _error("rate_limited", 429)
    result_payload = {"feature": command.payload.feature, "unit": command.payload.unit,
                      "lineage": command.payload.lineage, **asdict(result)}
    if forecast_result is not None:
        result_payload["forecast"] = asdict(forecast_result)
    if trend_result is not None:
        result_payload["trend"] = asdict(trend_result)
    output = {
        "schemaVersion": "openconnect.v1", "taskId": command.taskId,
        "kind": command.kind, "result": result_payload,
    }
    if cloud:
        output["usage"] = {"computeMilliseconds": compute_ms}
    return JSONResponse(output)


@app.post("/internal/cloud/v1/analytics/execute")
async def execute_cloud(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authenticated(request):
        return _error("unauthorized", 401)
    if os.environ.get("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "false").lower() != "true":
        return _error("capability_unavailable", 503)
    try:
        consumer = _cloud_consumer()
    except (OSError, ValueError, TypeError):
        return _error("capability_unavailable", 503)
    if consumer is None:
        return _error("capability_unavailable", 503)
    body = bytearray()
    try:
        async with asyncio.timeout(10):
            async for chunk in request.stream():
                if len(body) + len(chunk) > 350000:
                    return _error("invalid_request", 413)
                body.extend(chunk)
        from openconnect_analytics_inference.cloud_execution import execute_claimed

        command = await run_in_threadpool(execute_claimed, bytes(body), consumer, lambda value: value)
    except TimeoutError:
        return _error("invalid_request", 408)
    finally:
        body[:] = bytes(len(body))
    if command is None:
        return _error("capability_unavailable", 503)
    return await _execute_command(command, cloud=True)


@app.get("/internal/cloud/v1/analytics/ready")
async def cloud_ready(request: Request) -> JSONResponse:
    if not _authenticated(request):
        return _error("unauthorized", 401)
    if os.environ.get("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "false").lower() != "true" or not _ready():
        return _error("capability_unavailable", 503)
    try:
        consumer = _cloud_consumer()
        deployment = await run_in_threadpool(consumer.ready_deployment) if consumer is not None else None
    except (OSError, ValueError, TypeError):
        deployment = None
    if deployment is None:
        return _error("capability_unavailable", 503)
    return JSONResponse({"ready": True, "deploymentId": deployment})
