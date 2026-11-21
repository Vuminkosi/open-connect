"""Private HTTP API for the NLLB translation worker.

Lease stubs (claim/heartbeat/complete/fail) are minimal in-memory controls
for local development and contract tests; durable fencing lives host-side.
There is no remote fallback: translation serves only from the mounted
checkpoint (real mode) or the explicit CI stub (fake mode).
"""

from __future__ import annotations

import hmac
import logging
import os
import threading
import time
import uuid
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from fastapi.responses import JSONResponse

from openconnect_nllb.config import Settings
from openconnect_nllb import engine_mode
from openconnect_nllb.engine import (
    FAKE_TOKENIZER_HASH,
    FAKE_WARNING,
    FakeEngine,
    InputTooLongError,
    LanguageUnsupportedError,
    LicenseDeniedError,
    ModelUnavailableError,
    RealEngine,
)
from openconnect_nllb.decoder import DecoderInputError, DecoderUnavailable, OfflineNllbDecoder
from openconnect_nllb.decoder_bundle import resolve_decoder_bundle
from openconnect_nllb.languages import SUPPORTED_LANGUAGES, SUPPORTED_PAIRS
from openconnect_nllb.manifest import ModelManifest, Readiness
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
_decoder_lock = threading.Lock()
_cached_decoder: OfflineNllbDecoder | None = None
_cached_key: tuple[str, str] | None = None


def _real_readiness():
    global _cached_decoder, _cached_key
    if (
        not settings.model_id
        or settings.model_device == "cuda"
        or os.environ.get("HF_HUB_OFFLINE") != "1"
    ):
        return Readiness(False, "model_unavailable", None), "model_unavailable", None
    bundle, reason = resolve_decoder_bundle(
        settings.model_dir, settings.model_id, license_accepted=settings.license_accepted,
        commercial_use=settings.commercial_use,
    )
    if bundle is None:
        return Readiness(False, reason or "model_unavailable", None), reason or "model_unavailable", None
    manifest = ModelManifest(bundle.model_id, bundle.revision, bundle.weights_hash,
                             bundle.tokenizer_hash, bundle.license)
    key = (str(bundle.assets_dir), bundle.fingerprint)
    with _decoder_lock:
        if _cached_key != key or _cached_decoder is None:
            _cached_decoder = None
            _cached_key = None
            try:
                decoder = OfflineNllbDecoder(bundle)
                decoder.translate("Hello.", "eng_Latn", "fra_Latn")
            except (DecoderUnavailable, DecoderInputError):
                return Readiness(False, "model_unavailable", manifest), "model_unavailable", None
            _cached_decoder = decoder
            _cached_key = key
        return Readiness(True, None, manifest), None, _cached_decoder


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    info, _, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        return {"status": "unready", "ready": False, "reason": code}
    assert info is not None
    if settings.fake_engine:
        return {
            "status": "ready",
            "ready": True,
            "engine": "fake",
            "engineWarning": FAKE_WARNING,
            "modelRevision": "fake-nllb-0.1.0",
            "supportedLanguages": list(SUPPORTED_LANGUAGES),
            "provisioning": settings.provisioning,
            "modelCacheDir": settings.model_dir.as_posix(),
            "device": info.effective,
            "deviceRequested": info.requested,
        }
    result, reason, _ = _real_readiness()
    if result.ready and reason is None and result.manifest is not None:
        return {
            "status": "ready",
            "ready": True,
            "engine": "real",
            "modelRevision": result.manifest.revision,
            "tokenizerHash": result.manifest.tokenizer_hash,
            "provisioning": settings.provisioning,
            "modelCacheDir": settings.model_dir.as_posix(),
            "device": "cpu",
            "deviceRequested": info.requested,
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
    result, reason, decoder = _real_readiness()
    return RealEngine(result.manifest, ready=(result.ready and reason is None), reason=reason, decoder=decoder)


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
    result, reason, _ = _real_readiness()
    return CapabilitiesResponse(
        engine="real",
        modelRevision=result.manifest.revision if result.manifest else None,
        tokenizerHash=result.manifest.tokenizer_hash if result.manifest else None,
        capabilities=[Capability(languagePairs=pairs)] if result.ready and reason is None else [],
    )


@app.post(
    "/internal/v1/execute",
    response_model=ExecuteResponse,
    dependencies=[Depends(authenticate)],
)
def execute(request: ExecuteRequest) -> ExecuteResponse:
    _, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        return JSONResponse(
            status_code=status_code or status.HTTP_503_SERVICE_UNAVAILABLE,
            content={
                "schemaVersion": "openconnect.v1",
                "taskId": request.taskId,
                "status": "failed",
                "error": {"code": code, "message": code.replace("_", " ")},
            },
        )  # type: ignore[return-value]
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
    except InputTooLongError:
        return JSONResponse(
            status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
            content={"schemaVersion": "openconnect.v1", "taskId": request.taskId,
                     "status": "failed", "error": {"code": "input_too_long", "message": "input exceeds worker limit"}},
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
        logger.error("Translation failed unexpectedly")
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
