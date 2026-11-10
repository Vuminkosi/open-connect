"""Private HTTP API for the classification worker.

Lease stubs (claim/heartbeat/complete/fail) are minimal in-memory controls
for local development and contract tests; durable fencing lives host-side.
"""

from __future__ import annotations

import hmac
import logging
import time
import uuid
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status
from fastapi.responses import JSONResponse

from openconnect_classification.config import Settings
from openconnect_classification.engine import (
    FAKE_WARNING,
    FakeEngine,
    LicenseDeniedError,
    ModelUnavailableError,
    RealEngine,
)
from openconnect_classification import engine_mode
from openconnect_classification.label_sets import (
    DEFAULT_LABEL_SET_VERSION,
    get_label_set,
    label_set_versions,
)
from openconnect_classification.manifest import check_readiness
from openconnect_classification.schemas import (
    CapabilitiesResponse,
    Capability,
    ClassifyResult,
    ClaimRequest,
    ClaimResponse,
    CompleteRequest,
    ErrorBody,
    ExecuteRequest,
    ExecuteResponse,
    FailRequest,
    HeartbeatRequest,
)

logger = logging.getLogger("openconnect_classification")
app = FastAPI(title="OpenConnect Classification Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
_fake_engine = FakeEngine()

# taskId -> {leaseId, fencingToken, expiresAt}
_leases: dict[str, dict[str, object]] = {}
LEASE_TTL_SECONDS = 60


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    info, _, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        return {"status": "unready", "ready": False, "reason": code}
    assert info is not None
    device = {"device": info.effective, "deviceRequested": info.requested}
    if settings.fake_engine:
        return {
            "status": "ready",
            "ready": True,
            "engine": "fake",
            "engineWarning": FAKE_WARNING,
            "modelRevision": "fake-0.1.0",
            "labelSetVersion": DEFAULT_LABEL_SET_VERSION,
            **device,
        }
    result = check_readiness(settings.model_dir, license_accepted=settings.license_accepted)
    if result.ready and result.manifest is not None:
        return {
            "status": "ready",
            "ready": True,
            "engine": "real",
            "modelRevision": result.manifest.revision,
            **device,
        }
    return {"status": "unready", "ready": False, "reason": result.reason}


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
    result = check_readiness(settings.model_dir, license_accepted=settings.license_accepted)
    return RealEngine(result.manifest, ready=result.ready, reason=result.reason)


@app.get(
    "/internal/v1/capabilities",
    response_model=CapabilitiesResponse,
    dependencies=[Depends(authenticate)],
)
def capabilities() -> CapabilitiesResponse:
    if settings.fake_engine:
        return CapabilitiesResponse(
            engine="fake",
            modelRevision="fake-0.1.0",
            capabilities=[
                Capability(
                    labelSets=label_set_versions(), defaultLabelSet=DEFAULT_LABEL_SET_VERSION
                )
            ],
            fakeEngineWarning=FAKE_WARNING,
        )
    result = check_readiness(settings.model_dir, license_accepted=settings.license_accepted)
    return CapabilitiesResponse(
        engine="real",
        modelRevision=result.manifest.revision if result.manifest else None,
        capabilities=[
            Capability(labelSets=label_set_versions(), defaultLabelSet=DEFAULT_LABEL_SET_VERSION)
        ],
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
        get_label_set(payload.labelSetVersion)
    except KeyError:
        return JSONResponse(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            content={
                "schemaVersion": "openconnect.v1",
                "taskId": request.taskId,
                "status": "failed",
                "error": {
                    "code": "label_set_unknown",
                    "message": f"unknown labelSetVersion: {payload.labelSetVersion}",
                },
            },
        )  # type: ignore[return-value]
    try:
        outcome = _engine().classify(
            text,
            label_set_version=payload.labelSetVersion,
            threshold=payload.threshold,
            max_labels=payload.maxLabels,
            locale_hint=payload.localeHint,
        )
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
    except ModelUnavailableError:
        return JSONResponse(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            content={
                "schemaVersion": "openconnect.v1",
                "taskId": request.taskId,
                "status": "failed",
                "error": {"code": "model_unavailable", "message": "model unavailable"},
            },
        )  # type: ignore[return-value]
    except Exception:
        logger.exception("Classification failed task_id=%s", request.taskId)
        return ExecuteResponse(
            taskId=request.taskId,
            status="failed",
            error=ErrorBody(code="invalid_request", message="classification failed"),
        )
    return ExecuteResponse(
        taskId=request.taskId,
        status="completed",
        result=ClassifyResult(
            labels=outcome.labels,
            scores=outcome.scores,
            abstained=outcome.abstained,
            modelRevision=outcome.model_revision,
            labelSetVersion=payload.labelSetVersion,
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
