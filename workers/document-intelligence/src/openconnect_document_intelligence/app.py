"""Private document API, available only inside the required restricted runtime."""

from __future__ import annotations

import asyncio
import base64
import binascii
import json
import os
import re
import threading
from contextlib import asynccontextmanager
from dataclasses import asdict, dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Literal

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, ValidationError

from openconnect_document_intelligence.malware import (
    ClamAvScanner, MalwareDetectedError, MalwareScanUnavailableError,
)
from openconnect_document_intelligence.ocr import MAX_INPUT_BYTES, TesseractOcr
from openconnect_document_intelligence.pipeline import DocumentAnalyzer
from openconnect_worker_common.contracts.capability import (
    CapabilityDescriptor, CapabilityKind, CapabilityState,
)
from openconnect_worker_common.models.resource_admission import (
    AdmissionVerdict, ResourceBudget, ResourceRequest, admit,
)
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.signing import key_from_environment, verify_bytes

MAX_BODY_BYTES = 14 * 1024 * 1024
MAX_RESPONSE_BYTES = 8 * 1024 * 1024
_slot = threading.BoundedSemaphore(1)


class ServiceConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")
    manifest: RuntimeModelManifest
    resourceRequest: ResourceRequest
    resourceBudget: ResourceBudget
    tesseractPath: str = Field(min_length=1, max_length=1024)
    clamSocket: str = Field(min_length=1, max_length=1024)
    licenseAccepted: bool = False
    commercialUse: bool = False


class Payload(BaseModel):
    model_config = ConfigDict(extra="forbid")
    contentBase64: str = Field(min_length=1, max_length=((MAX_INPUT_BYTES + 2) // 3) * 4)


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal["documents.ocr"]
    tenantId: str = Field(pattern=r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: Payload


@dataclass(frozen=True)
class Runtime:
    scanner: ClamAvScanner
    ocr: TesseractOcr
    analyzer: DocumentAnalyzer


def _sandbox_ready() -> bool:
    """Check minimum restrictions; deployment must additionally enforce isolation."""
    try:
        if os.name != "posix" or not hasattr(os, "geteuid") or os.geteuid() == 0:
            return False
        status = Path("/proc/self/status").read_text(encoding="utf-8")
        fields = dict(line.split(":", 1) for line in status.splitlines() if ":" in line)
        if fields.get("NoNewPrivs", "").strip() != "1" or fields.get("Seccomp", "").strip() != "2":
            return False
        if set(os.listdir("/sys/class/net")) != {"lo"}:
            return False
        if not (os.statvfs("/").f_flag & os.ST_RDONLY):
            return False
        memory = Path("/sys/fs/cgroup/memory.max").read_text().strip()
        processes = Path("/sys/fs/cgroup/pids.max").read_text().strip()
        return 0 < int(memory) <= 2 * 1024**3 and 0 < int(processes) <= 128
    except (OSError, ValueError, AttributeError):
        return False


def _load_runtime() -> Runtime | None:
    if (os.environ.get("OC_DOCUMENT_ENABLED", "false").lower() != "true"
            or os.environ.get("OC_MODEL_PROVISIONING", "first-start-if-missing") == "disabled"
            or os.environ.get("OC_WORKER_ENGINE", "real").lower() != "real"
            or os.environ.get("OC_MODEL_DEVICE", "auto").lower() not in {"auto", "cpu"}
            or not _sandbox_ready()):
        return None
    try:
        path = Path(os.environ["OC_DOCUMENT_CONFIG_PATH"])
        if path.stat().st_size > 1024 * 1024:
            return None
        config = ServiceConfig.model_validate_json(path.read_bytes())
        required = config.resourceRequest
        if (config.manifest.workerKind != "document-intelligence" or required.memoryMb <= 0
                or required.cpuMillicores <= 0 or required.gpuRequired or required.vramMb > 0
                or admit(required, config.resourceBudget).verdict != AdmissionVerdict.ADMIT):
            return None
        scanner = ClamAvScanner(Path(config.clamSocket))
        scanner.scan(b"OpenConnect readiness probe", timeout_seconds=2)
        ocr = TesseractOcr(RuntimeModelCacheManager(
            Path(os.environ.get("OC_MODEL_CACHE_DIR", "/models")), "document-intelligence"),
            config.manifest, Path(config.tesseractPath), license_accepted=config.licenseAccepted,
            commercial_use=config.commercialUse)
        return Runtime(scanner, ocr, DocumentAnalyzer(scanner, ocr))
    except Exception:
        return None


@asynccontextmanager
async def lifespan(app: FastAPI):
    app.state.runtime = await run_in_threadpool(_load_runtime)
    yield
    app.state.runtime = None


app = FastAPI(title="OpenConnect Document Worker", docs_url=None, redoc_url=None,
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
        runtime.ocr._verify()
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
    descriptor = CapabilityDescriptor(id="documents.ocr", kind=CapabilityKind.TOOL,
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
        async with asyncio.timeout(15):
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
        content = base64.b64decode(command.payload.contentBase64, validate=True)
        if not content or len(content) > MAX_INPUT_BYTES:
            return _error("invalid_request", 422)
    except (ValidationError, ValueError, binascii.Error):
        return _error("invalid_request", 422)
    runtime = getattr(app.state, "runtime", None)
    if runtime is None or not _sandbox_ready():
        return _error("capability_unavailable", 503)
    if not _slot.acquire(blocking=False):
        return _error("rate_limited", 429)
    try:
        remaining = (command.expiresAt - datetime.now(timezone.utc)).total_seconds()
        if remaining <= 0:
            return _error("invalid_request", 422)
        result = await run_in_threadpool(runtime.analyzer.analyze, content, timeout_seconds=remaining)
    except MalwareDetectedError:
        return _error("malware_detected", 422)
    except MalwareScanUnavailableError:
        return _error("capability_unavailable", 503)
    except ValueError:
        return _error("invalid_request", 422)
    except Exception:
        return _error("capability_unavailable", 503)
    finally:
        _slot.release()
    if datetime.now(timezone.utc) >= command.expiresAt:
        return _error("invalid_request", 422)
    payload = {"schemaVersion": "openconnect.v1", "taskId": command.taskId,
               "kind": command.kind, "result": asdict(result)}
    output = json.dumps(payload, ensure_ascii=True, allow_nan=False, separators=(",", ":")).encode()
    if len(output) > MAX_RESPONSE_BYTES:
        return _error("capability_unavailable", 503)
    return JSONResponse(payload)
