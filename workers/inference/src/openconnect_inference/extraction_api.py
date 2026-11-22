"""Private extraction route for a host-installed, already-provisioned local provider."""

from __future__ import annotations

import asyncio
import json
import os
import re
import threading
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Literal

from fastapi import APIRouter, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, ValidationError

from openconnect_inference.extraction import ExtractionError, ExtractionProvider, _unique_object, extract
from openconnect_inference.engine_mode import is_production, resolve_environment
from openconnect_inference.extraction_llama import LlamaExtractionProvider
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.signing import key_from_environment, verify_bytes

MAX_BODY_BYTES = 512 * 1024
_slot = threading.BoundedSemaphore(1)
router = APIRouter()


class Payload(BaseModel):
    model_config = ConfigDict(extra="forbid")
    document: str = Field(min_length=1, max_length=100_000)
    extractionSchema: dict[str, Any]


class ExtractionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["documents.extract"]
    tenantId: str = Field(min_length=26, max_length=26, pattern=r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: Payload


class ExtractionRuntime:
    """Host-owned provider and verifier; no model provisioning or egress authorization is performed here."""

    def __init__(self, provider: ExtractionProvider, verify: Callable[[], bool]):
        if provider is None or not callable(getattr(provider, "complete", None)) or not callable(verify):
            raise ValueError("invalid_extraction_runtime")
        self.provider = provider
        self.verify = verify


def ready(app) -> bool:
    if os.environ.get("OC_EXTRACTION_ENABLED", "false").lower() != "true":
        return False
    key = key_from_environment()
    runtime = getattr(app.state, "extraction_runtime", None)
    if key is None or len(key) < 32 or not isinstance(runtime, ExtractionRuntime):
        return False
    try:
        if is_production() or resolve_environment().lower() == "production":
            from llama_cpp import Llama

            if not isinstance(runtime.provider, LlamaExtractionProvider) or not isinstance(runtime.provider._model, Llama):
                return False
        return runtime.verify() is True
    except Exception:
        return False


def _error(code: str, status: int) -> JSONResponse:
    return JSONResponse({"error": {"code": code}}, status_code=status)


def _execute(runtime: ExtractionRuntime, command: ExtractionRequest, deadline: float):
    if not _slot.acquire(blocking=False):
        raise BlockingIOError("busy")
    try:
        if time.monotonic() >= deadline or runtime.verify() is not True:
            raise RuntimeError("unavailable")
        return extract(command.payload.document, command.payload.extractionSchema, runtime.provider)
    finally:
        _slot.release()


@router.post("/internal/v1/extract")
async def execute(request: Request) -> JSONResponse:
    token = os.environ.get("OPENCONNECT_INFERENCE_TOKEN") or os.environ.get("OPENCONNECT_WORKER_TOKEN")
    key = key_from_environment()
    if not token or key is None or len(key) < 32:
        return _error("capability_unavailable", 503)
    try:
        authorized = verify_bearer(request.headers.get("authorization"), token)
    except TypeError:
        authorized = False
    if not authorized:
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
    signature = request.headers.get("x-openconnect-signature", "")
    if not re.fullmatch(r"[0-9a-f]{64}", signature) or not verify_bytes(bytes(body), signature, key):
        return _error("unauthorized", 401)
    try:
        parsed = json.loads(body, object_pairs_hook=_unique_object,
                            parse_constant=lambda _: (_ for _ in ()).throw(ValueError("invalid_json")))
        command = ExtractionRequest.model_validate(parsed)
        now = datetime.now(timezone.utc)
        if (command.issuedAt > now or command.expiresAt <= now or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > timedelta(minutes=5)):
            return _error("invalid_request", 422)
    except (ValidationError, ValueError, RecursionError):
        return _error("invalid_request", 422)
    if not ready(request.app):
        return _error("capability_unavailable", 503)
    runtime = request.app.state.extraction_runtime
    deadline = time.monotonic() + (command.expiresAt - datetime.now(timezone.utc)).total_seconds()
    try:
        async with asyncio.timeout_at(deadline):
            result = await run_in_threadpool(_execute, runtime, command, deadline)
    except TimeoutError:
        return _error("invalid_request", 422)
    except BlockingIOError:
        return _error("rate_limited", 429)
    except ExtractionError as exc:
        return _error("capability_unavailable", 503) if str(exc) == "provider_unavailable" else _error("invalid_request", 422)
    except Exception:
        return _error("capability_unavailable", 503)
    if time.monotonic() >= deadline:
        return _error("invalid_request", 422)
    return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                         "kind": command.kind, "result": result.model_dump(mode="json")})
