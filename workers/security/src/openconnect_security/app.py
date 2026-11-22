"""Private HTTP API for security scanning (rules + optional model-backed detectors)."""

from __future__ import annotations

import hmac
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status

from openconnect_security import engine_mode
from openconnect_security.config import Settings
from openconnect_security.detectors.rules import scan as scan_text
from openconnect_security.schemas import ScanRequest, ScanResponse

logger = logging.getLogger("openconnect_security")
app = FastAPI(title="OpenConnect Security Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
RULES_DETECTOR_VERSION = "1.0.0"


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    info, _, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        return {"status": "unready", "ready": False, "reason": code, "capabilities": []}
    assert info is not None
    enabled = settings.enabled_detectors()
    if not enabled:
        return {
            "status": "unready",
            "ready": False,
            "reason": "worker_disabled",
            "capabilities": [],
        }
    model_detectors = settings.enabled_model_detectors()
    if model_detectors:
        return {
            "status": "unready",
            "ready": False,
            "reason": "detector_not_implemented",
            "missingDetectors": model_detectors,
            "capabilities": ["rules"] if settings.rules_enabled else [],
        }
    if not settings.fake_engine:
        # Rules-only default profile: preserve exact compact readiness contract.
        return {"status": "ready", "capabilities": ["rules"]}
    return {
        "status": "ready",
        "ready": True,
        "engine": "fake",
        "capabilities": enabled,
        "provisioning": settings.provisioning,
        "modelCacheDir": settings.model_dir.as_posix(),
        "device": info.effective,
        "deviceRequested": info.requested,
    }


def authenticate(authorization: Annotated[str | None, Header()] = None) -> None:
    token = settings.scan_token
    if token is None:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="scanner unavailable"
        )
    supplied = authorization.removeprefix("Bearer ") if authorization else ""
    if (
        not authorization
        or not authorization.startswith("Bearer ")
        or not hmac.compare_digest(
            supplied.encode("utf-8", "surrogatepass"), token.encode("utf-8", "surrogatepass")
        )
    ):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="unauthorized")


@app.post("/scan", response_model=ScanResponse, dependencies=[Depends(authenticate)])
def scan(request: ScanRequest) -> ScanResponse:
    _, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(
            status_code=status_code or status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=code,
        )
    enabled = set(settings.enabled_detectors())
    for detector in request.detectors:
        if detector not in enabled or detector != "rules":
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="detector unavailable"
            )
    try:
        spans = scan_text(request.text)
    except Exception:
        logger.exception("Security scan failed request_id=%s", request.requestId)
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="scanner unavailable"
        ) from None
    return ScanResponse(
        schemaVersion="openconnect.v1",
        requestId=request.requestId,
        detectorId="rules",
        detectorVersion=RULES_DETECTOR_VERSION,
        spans=spans,
    )
