"""Private ClamAV-screened vision API for a restricted local runtime."""

from __future__ import annotations

import asyncio
import base64
import binascii
import io
import os
import re
import threading
import time
from contextlib import asynccontextmanager
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Literal

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from PIL import Image
from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, StrictInt, ValidationError

from openconnect_vision.classifier import MAX_INPUT_BYTES, OnnxImageClassifier
from openconnect_worker_common.contracts.capability import CapabilityDescriptor, CapabilityKind, CapabilityState
from openconnect_worker_common.contracts.strict_json import parse_signed_json
from openconnect_worker_common.models.resource_admission import AdmissionVerdict, ResourceBudget, ResourceRequest, admit
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest
from openconnect_worker_common.security.clamav import ClamAvScanner, MalwareDetectedError, MalwareScanUnavailableError
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.sandbox import restricted_media_sandbox_ready as _sandbox_ready
from openconnect_worker_common.security.signing import key_from_environment, verify_bytes

MAX_BODY_BYTES = 14 * 1024 * 1024
_slot = threading.BoundedSemaphore(1)


class ServiceConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")
    manifest: RuntimeModelManifest
    resourceRequest: ResourceRequest
    resourceBudget: ResourceBudget
    modelAsset: str = Field(min_length=1, max_length=512)
    labelsAsset: str = Field(min_length=1, max_length=512)
    taxonomyVersion: str = Field(min_length=1, max_length=128)
    taxonomyApproved: bool = False
    clamSocket: str = Field(min_length=1, max_length=1024)
    licenseAccepted: bool = False
    commercialUse: bool = False


class Payload(BaseModel):
    model_config = ConfigDict(extra="forbid")
    contentBase64: str = Field(min_length=1, max_length=((MAX_INPUT_BYTES + 2) // 3) * 4, repr=False)
    topK: StrictInt = Field(default=5, ge=1, le=20)


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128, repr=False)
    kind: Literal["vision.analyze"]
    tenantId: str = Field(min_length=26, max_length=26, pattern=r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$", repr=False)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: Payload


@dataclass(frozen=True)
class Runtime:
    scanner: ClamAvScanner
    classifier: OnnxImageClassifier


def _load_runtime() -> Runtime | None:
    if (os.environ.get("OC_VISION_ENABLED", "false").lower() != "true"
            or os.environ.get("OC_MODEL_PROVISIONING", "first-start-if-missing") == "disabled"
            or os.environ.get("OC_WORKER_ENGINE", "real").lower() != "real"
            or os.environ.get("OC_MODEL_DEVICE", "auto").lower() not in {"auto", "cpu"}
            or not _sandbox_ready()):
        return None
    try:
        path = Path(os.environ["OC_VISION_CONFIG_PATH"])
        if path.stat().st_size > 1024 * 1024:
            return None
        config = ServiceConfig.model_validate_json(path.read_bytes())
        required = config.resourceRequest
        if (config.manifest.workerKind != "vision" or required.memoryMb <= 0 or required.cpuMillicores <= 0
                or required.gpuRequired or required.vramMb > 0
                or admit(required, config.resourceBudget).verdict != AdmissionVerdict.ADMIT):
            return None
        scanner = ClamAvScanner(Path(config.clamSocket))
        scanner.scan(b"OpenConnect readiness probe", timeout_seconds=2)
        classifier = OnnxImageClassifier(RuntimeModelCacheManager(
            Path(os.environ.get("OC_MODEL_CACHE_DIR", "/models")), "vision"), config.manifest,
            model_asset=config.modelAsset, labels_asset=config.labelsAsset, taxonomy_version=config.taxonomyVersion,
            taxonomy_approved=config.taxonomyApproved, license_accepted=config.licenseAccepted,
            commercial_use=config.commercialUse)
        probe = io.BytesIO()
        Image.new("RGB", (224, 224), "white").save(probe, format="PNG")
        classifier.classify(probe.getvalue(), top_k=1)
        return Runtime(scanner, classifier)
    except Exception:
        return None


@asynccontextmanager
async def lifespan(app: FastAPI):
    app.state.runtime = await run_in_threadpool(_load_runtime)
    yield
    app.state.runtime = None


app = FastAPI(title="OpenConnect Vision Worker", docs_url=None, redoc_url=None,
              openapi_url=None, lifespan=lifespan)


def _configured() -> bool:
    key = key_from_environment()
    return bool(os.environ.get("OPENCONNECT_WORKER_TOKEN")) and bool(key and len(key) >= 32)


def _ready() -> bool:
    runtime = getattr(app.state, "runtime", None)
    if not _configured() or runtime is None or not _sandbox_ready():
        return False
    try:
        runtime.scanner.scan(b"OpenConnect readiness probe", timeout_seconds=2)
        runtime.classifier._verify()
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
    descriptor = CapabilityDescriptor(id="vision.analyze", kind=CapabilityKind.IMAGE,
                                      state=CapabilityState.READY, version="1.0.0", localOnly=True)
    return JSONResponse({"capabilities": [descriptor.model_dump(mode="json")]})


class _Busy(Exception):
    pass


def _execute(runtime: Runtime, content: bytes, top_k: int, deadline: float):
    if not _slot.acquire(blocking=False):
        raise _Busy()
    try:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ValueError("expired")
        runtime.scanner.scan(content, timeout_seconds=min(remaining, 120))
        if deadline <= time.monotonic():
            raise ValueError("expired")
        return runtime.classifier.classify(content, top_k=top_k)
    finally:
        _slot.release()


@app.post("/internal/v1/execute")
async def execute(request: Request) -> JSONResponse:
    if not _configured():
        return _error("capability_unavailable", 503)
    if not _authorized(request):
        return _error("unauthorized", 401)
    body = bytearray()
    try:
        async with asyncio.timeout(15):
            async for chunk in request.stream():
                if len(body) + len(chunk) > MAX_BODY_BYTES:
                    return _error("invalid_request", 413)
                body.extend(chunk)
    except TimeoutError:
        return _error("invalid_request", 408)
    signature, key = request.headers.get("x-openconnect-signature", ""), key_from_environment()
    if not key or not re.fullmatch(r"[0-9a-f]{64}", signature) or not verify_bytes(bytes(body), signature, key):
        return _error("unauthorized", 401)
    try:
        command = ExecuteRequest.model_validate(parse_signed_json(body))
        now = datetime.now(timezone.utc)
        if (command.issuedAt > now or command.expiresAt <= now or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > timedelta(minutes=5)):
            return _error("invalid_request", 422)
        content = base64.b64decode(command.payload.contentBase64, validate=True)
        if not content or len(content) > MAX_INPUT_BYTES:
            return _error("invalid_request", 422)
    except (ValidationError, ValueError, binascii.Error, RecursionError):
        return _error("invalid_request", 422)
    runtime = getattr(app.state, "runtime", None)
    if runtime is None or not _sandbox_ready():
        return _error("capability_unavailable", 503)
    deadline = time.monotonic() + (command.expiresAt - datetime.now(timezone.utc)).total_seconds()
    try:
        result = await run_in_threadpool(_execute, runtime, content, command.payload.topK, deadline)
    except _Busy:
        return _error("rate_limited", 429)
    except MalwareDetectedError:
        return _error("malware_detected", 422)
    except MalwareScanUnavailableError:
        return _error("capability_unavailable", 503)
    except ValueError:
        return _error("invalid_request", 422)
    except Exception:
        return _error("capability_unavailable", 503)
    if time.monotonic() >= deadline:
        return _error("invalid_request", 422)
    return JSONResponse({"schemaVersion": "openconnect.v1", "taskId": command.taskId,
                         "kind": command.kind, "result": asdict(result)})
