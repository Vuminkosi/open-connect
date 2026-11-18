"""Private HTTP API for security scanning (rules + optional model-backed detectors)."""

from __future__ import annotations

import hmac
import json
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status

from openconnect_security import engine_mode
from openconnect_security.config import MODEL_BACKED_DETECTORS, Settings
from openconnect_security.detectors.rules import scan as scan_text
from openconnect_security.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest
from openconnect_security.schemas import ScanRequest, ScanResponse

logger = logging.getLogger("openconnect_security")
app = FastAPI(title="OpenConnect Security Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()


def _verify_model_detector(detector_id: str, current_settings: Settings) -> tuple[bool, str | None]:
    """Verify whether a model-backed detector has a verified revision in the local cache.

    Never downloads models at request/readiness time.
    """
    model_id = MODEL_BACKED_DETECTORS.get(detector_id)
    if not model_id:
        return False, "model_unavailable"
    mgr = RuntimeModelCacheManager(current_settings.model_dir, "security")
    active = mgr.read_active(model_id)
    if active is None:
        return False, "model_unavailable"
    fp = str(active.get("activeFingerprint", ""))
    paths = mgr.paths_for(model_id)
    lock_path = paths.manifest_lock_path(fp)
    if not fp or not lock_path.is_file():
        return False, "cache_corrupt"
    try:
        lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
        manifest = RuntimeModelManifest.model_validate(lock_data["manifest"])
    except Exception:
        return False, "cache_corrupt"
    result = mgr.verify_for_request(
        manifest,
        license_accepted=current_settings.license_accepted,
    )
    return result.ready, result.reason


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
    if not model_detectors and not settings.fake_engine:
        # Rules-only default profile: preserve exact compact readiness contract.
        return {"status": "ready", "capabilities": ["rules"]}
    if settings.fake_engine:
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
    missing: list[str] = []
    first_reason: str | None = None
    for det in model_detectors:
        ok, reason = _verify_model_detector(det, settings)
        if not ok:
            missing.append(det)
            if first_reason is None:
                first_reason = reason or "model_unavailable"
    if missing:
        return {
            "status": "unready",
            "ready": False,
            "reason": first_reason or "model_unavailable",
            "missingDetectors": missing,
            "capabilities": ["rules"] if settings.rules_enabled else [],
        }
    return {
        "status": "ready",
        "ready": True,
        "engine": "real",
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
        or not hmac.compare_digest(supplied, token)
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
        if detector not in enabled:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail="detector unavailable"
            )
        if detector != "rules" and not settings.fake_engine:
            ok, _ = _verify_model_detector(detector, settings)
            if not ok:
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
        detectorId="rules" if request.detectors == ["rules"] else ",".join(request.detectors),
        detectorVersion="1.0.0",
        spans=spans,
    )
