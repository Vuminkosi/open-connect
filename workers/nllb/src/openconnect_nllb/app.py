"""Private HTTP API for the NLLB translation worker.

Lease stubs (claim/heartbeat/complete/fail) are minimal in-memory controls
for local development and contract tests; durable fencing lives host-side.
There is no remote fallback: translation serves only from the mounted
checkpoint (real mode) or the explicit CI stub (fake mode).
"""

from __future__ import annotations

import hmac
import logging
import time
import uuid
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from fastapi.responses import JSONResponse

from openconnect_nllb.config import Settings
from openconnect_nllb.engine import (
    FAKE_TOKENIZER_HASH,
    FAKE_WARNING,
    FakeEngine,
    LanguageUnsupportedError,
    LicenseDeniedError,
    ModelUnavailableError,
    RealEngine,
    decoder_available,
)
from openconnect_nllb.languages import SUPPORTED_LANGUAGES, SUPPORTED_PAIRS
from openconnect_nllb.manifest import check_readiness
from openconnect_nllb.schemas import (
    CapabilitiesResponse,
    Capability,
    ClaimRequest,
    ClaimResponse,
    CompleteRequest,
    ErrorBody,
    ExecuteRequest,
    ExecuteResponse,
    FailRequest,
    HeartbeatRequest,
    TranslateResult,
)

logger = logging.getLogger("openconnect_nllb")
app = FastAPI(title="OpenConnect NLLB Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
_fake_engine = FakeEngine()

# taskId -> {leaseId, fencingToken, expiresAtEpoch}
_leases: dict[str, dict[str, object]] = {}
LEASE_TTL_SECONDS = 60


def _real_readiness():
    result = check_readiness(settings.model_dir, license_accepted=settings.license_accepted)
    if result.ready and not decoder_available():
        return result, "model_unavailable"
    return result, result.reason


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    if settings.fake_engine:
        return {
            "status": "ready",
            "ready": True,
            "engine": "fake",
            "engineWarning": FAKE_WARNING,
            "modelRevision": "fake-nllb-0.1.0",
            "supportedLanguages": list(SUPPORTED_LANGUAGES),
        }
    result, reason = _real_readiness()
    if result.ready and result.manifest is not None:
        return {
            "status": "ready",
            "ready": True,
            "engine": "real",
            "modelRevision": result.manifest.revision,
            "tokenizerHash": result.manifest.tokenizer_hash,
        }
    return {"status": "unready", "ready": False, "reason": reason}


def authenticate(authorization: Annotated[str | None, Header()] = None) -> None:
    token = settings.worker_token
    if token is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="worker unavailable"
        )
    supplied = authorization.removeprefix("Bearer ") if authorization else ""
    if (
        not authorization
        or not authorization.startswith("Bearer ")
        or not hmac.compare_digest(supplied, token)
    ):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="unauthorized")


def _engine():
    if settings.fake_engine:
        return _fake_engine
    result, reason = _real_readiness()
    return RealEngine(result.manifest, ready=(result.ready and reason is None), reason=reason)


@app.get(
    "/internal/v1/capabilities",
    response_model=CapabilitiesResponse,
    dependencies=[Depends(authenticate)],
)
def capabilities() -> CapabilitiesResponse:
    pairs = sorted(f"{source}->{target}" for source, target in SUPPORTED_PAIRS)
    if settings.fake_engine:
        return CapabilitiesResponse(
            engine="fake",
            modelRevision="fake-nllb-0.1.0",
            tokenizerHash=FAKE_TOKENIZER_HASH,
            capabilities=[Capability(languagePairs=pairs)],
            fakeEngineWarning=FAKE_WARNING,
        )
    result, _ = _real_readiness()
    return CapabilitiesResponse(
        engine="real",
        modelRevision=result.manifest.revision if result.manifest else None,
        tokenizerHash=result.manifest.tokenizer_hash if result.manifest else None,
        capabilities=[Capability(languagePairs=pairs)],
    )


@app.post(
    "/internal/v1/execute",
    response_model=ExecuteResponse,
    dependencies=[Depends(authenticate)],
)
def execute(request: ExecuteRequest) -> ExecuteResponse:
    payload = request.input
    text = payload.text or ""
    if payload.inputRef and not payload.text:
        return ExecuteResponse(
            taskId=request.taskId,
            status="failed",
            error=ErrorBody(
                code="input_ref_unresolved", message="inputRef fetch is host-side only"
            ),
        )
    if len(text) > settings.max_text_chars:
        return JSONResponse(
            status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
            content={
                "schemaVersion": "openconnect.v1",
                "taskId": request.taskId,
                "status": "failed",
                "error": {"code": "input_too_long", "message": "input exceeds worker limit"},
            },
        )  # type: ignore[return-value]
    try:
        outcome = _engine().translate(
            text, source_lang=payload.sourceLang, target_lang=payload.targetLang
        )
    except LanguageUnsupportedError as exc:
        return JSONResponse(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            content={
                "schemaVersion": "openconnect.v1",
                "taskId": request.taskId,
                "status": "failed",
                "error": {"code": "language_unsupported", "message": str(exc)},
            },
        )  # type: ignore[return-value]
    except LicenseDeniedError:
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={
                "schemaVersion": "openconnect.v1",
                "taskId": request.taskId,
                "status": "failed",
                "error": {"code": "license_denied", "message": "model license not accepted"},
            },
        )  # type: ignore[return-value]
    except ModelUnavailableError as exc:
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={
                "schemaVersion": "openconnect.v1",
                "taskId": request.taskId,
                "status": "failed",
                "error": {"code": "model_unavailable", "message": str(exc)},
            },
        )  # type: ignore[return-value]
    except Exception:
        logger.exception("Translation failed task_id=%s", request.taskId)
        return ExecuteResponse(
            taskId=request.taskId,
            status="failed",
            error=ErrorBody(code="invalid_request", message="translation failed"),
        )
    return ExecuteResponse(
        taskId=request.taskId,
        status="completed",
        result=TranslateResult(
            translatedText=outcome.translated_text,
            sourceLang=outcome.source_lang,
            targetLang=outcome.target_lang,
            modelRevision=outcome.model_revision,
            tokenizerHash=outcome.tokenizer_hash,
            engine=outcome.engine,  # type: ignore[arg-type]
            engineWarning=FAKE_WARNING if outcome.engine == "fake" else None,
        ),
    )


def _valid_lease(task_id: str, lease_id: str) -> bool:
    lease = _leases.get(task_id)
    if lease is None:
        return False
    if lease.get("leaseId") != lease_id:
        return False
    if float(lease.get("expiresAtEpoch", 0)) < time.time():  # type: ignore[arg-type]
        return False
    return True


@app.post("/internal/v1/claim", response_model=ClaimResponse, dependencies=[Depends(authenticate)])
def claim(request: ClaimRequest) -> ClaimResponse:
    lease_id = uuid.uuid4().hex
    fencing = int(time.time() * 1000)
    expires = time.time() + LEASE_TTL_SECONDS
    _leases[request.taskId] = {
        "leaseId": lease_id,
        "fencingToken": fencing,
        "expiresAtEpoch": expires,
    }
    return ClaimResponse(
        taskId=request.taskId,
        leaseId=lease_id,
        fencingToken=fencing,
        expiresAt=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(expires)),
    )


@app.post("/internal/v1/heartbeat", dependencies=[Depends(authenticate)])
def heartbeat(request: HeartbeatRequest) -> dict[str, object]:
    if not _valid_lease(request.taskId, request.leaseId):
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="lease_expired")
    lease = _leases[request.taskId]
    expires = time.time() + LEASE_TTL_SECONDS
    lease["expiresAtEpoch"] = expires
    return {"taskId": request.taskId, "leaseId": request.leaseId, "renewed": True}


@app.post("/internal/v1/complete", dependencies=[Depends(authenticate)])
def complete(request: CompleteRequest) -> dict[str, object]:
    if not _valid_lease(request.taskId, request.leaseId):
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="lease_expired")
    _leases.pop(request.taskId, None)
    return {"taskId": request.taskId, "committed": True}


@app.post("/internal/v1/fail", dependencies=[Depends(authenticate)])
def fail(request: FailRequest) -> dict[str, object]:
    if not _valid_lease(request.taskId, request.leaseId):
        raise HTTPException(status_code=status.HTTP_409_CONFLICT, detail="lease_expired")
    _leases.pop(request.taskId, None)
    return {"taskId": request.taskId, "failed": True, "code": request.code}
