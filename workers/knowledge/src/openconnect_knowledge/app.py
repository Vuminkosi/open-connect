"""Private knowledge embedding endpoint; model loading is startup-only."""

from __future__ import annotations

import asyncio
import os
import re
import threading
from contextlib import asynccontextmanager
from dataclasses import asdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Literal

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, ValidationError

from openconnect_knowledge.embeddings import EmbeddingProfile, OnnxEmbeddingEngine
from openconnect_worker_common.contracts.capability import (
    CapabilityDescriptor, CapabilityKind, CapabilityState,
)
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.signing import key_from_environment, verify_bytes

MAX_BODY_BYTES = 128 * 1024
_slot = threading.BoundedSemaphore(1)


class Profile(BaseModel):
    model_config = ConfigDict(extra="forbid")
    modelAsset: str
    tokenizerAsset: str
    dimension: int = Field(ge=1, le=8192)
    maxTokens: int = Field(ge=2, le=512)
    prefixStyle: Literal["none", "e5"] = "none"
    padToken: str = "[PAD]"


class ServiceConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")
    manifest: RuntimeModelManifest
    profile: Profile
    licenseAccepted: bool = False
    commercialUse: bool = False


class Payload(BaseModel):
    model_config = ConfigDict(extra="forbid")
    inputKind: Literal["query", "passage"]
    texts: list[str] = Field(min_length=1, max_length=32)


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["knowledge.embed"]
    tenantId: str = Field(min_length=1, max_length=128)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: Payload


def _load_engine() -> OnnxEmbeddingEngine | None:
    if os.environ.get("OC_KNOWLEDGE_ENABLED", "false").lower() != "true":
        return None
    if os.environ.get("OC_WORKER_ENGINE", "real").lower() != "real":
        return None
    try:
        path = Path(os.environ["OC_KNOWLEDGE_CONFIG_PATH"])
        if path.stat().st_size > 1024 * 1024:
            return None
        config = ServiceConfig.model_validate_json(path.read_bytes())
        if config.manifest.workerKind != "knowledge":
            return None
        profile = config.profile
        engine = OnnxEmbeddingEngine(
            RuntimeModelCacheManager(Path(os.environ.get("OC_MODEL_CACHE_DIR", "/models")), "knowledge"),
            config.manifest, EmbeddingProfile(profile.modelAsset, profile.tokenizerAsset,
                                             profile.dimension, profile.maxTokens,
                                             profile.prefixStyle, profile.padToken),
            license_accepted=config.licenseAccepted, commercial_use=config.commercialUse,
        )
        engine.embed(["startup readiness probe"], input_kind="query")
        return engine
    except Exception:
        # Startup errors may contain paths or model internals; never expose them.
        return None


@asynccontextmanager
async def lifespan(app: FastAPI):
    app.state.engine = await run_in_threadpool(_load_engine)
    yield
    app.state.engine = None


app = FastAPI(title="OpenConnect Knowledge Worker", docs_url=None, redoc_url=None,
              openapi_url=None, lifespan=lifespan)


def _configured() -> bool:
    key = key_from_environment()
    return bool(os.environ.get("OPENCONNECT_WORKER_TOKEN")) and bool(key and len(key) >= 32)


def _ready() -> bool:
    engine = getattr(app.state, "engine", None)
    if not _configured() or engine is None:
        return False
    try:
        engine._verify()
        return True
    except Exception:
        return False


def _error(code: str, status: int) -> JSONResponse:
    return JSONResponse({"error": {"code": code}}, status_code=status)


def _authorized(request: Request) -> bool:
    try:
        return verify_bearer(request.headers.get("authorization"), os.environ.get("OPENCONNECT_WORKER_TOKEN"))
    except TypeError:
        return False


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> JSONResponse:
    ready_now = _ready()
    return JSONResponse({"ready": ready_now}, status_code=200 if ready_now else 503)


@app.get("/internal/v1/capabilities")
def capabilities(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authorized(request):
        return _error("unauthorized", 401)
    if not _ready():
        return JSONResponse({"capabilities": []})
    descriptor = CapabilityDescriptor(id="knowledge.embed", kind=CapabilityKind.EMBEDDING,
        state=CapabilityState.READY, version="1.0.0", localOnly=True)
    return JSONResponse({"capabilities": [descriptor.model_dump(mode="json")]})


@app.post("/internal/v1/execute")
async def execute(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authorized(request):
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
    key = key_from_environment()
    if not key or not re.fullmatch(r"[0-9a-f]{64}", signature) or not verify_bytes(bytes(body), signature, key):
        return _error("unauthorized", 401)
    try:
        command = ExecuteRequest.model_validate_json(body)
        now = datetime.now(timezone.utc)
        if (command.issuedAt > now or command.expiresAt <= now
                or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > timedelta(minutes=5)):
            return _error("invalid_request", 422)
    except (ValidationError, ValueError):
        return _error("invalid_request", 422)
    engine = getattr(app.state, "engine", None)
    if engine is None:
        return _error("capability_unavailable", 503)
    if not _slot.acquire(blocking=False):
        return _error("rate_limited", 429)
    try:
        result = await run_in_threadpool(engine.embed, command.payload.texts,
                                         input_kind=command.payload.inputKind)
    except ValueError:
        return _error("invalid_request", 422)
    except Exception:
        return _error("capability_unavailable", 503)
    finally:
        _slot.release()
    return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                         "kind": command.kind, "result": asdict(result)})
