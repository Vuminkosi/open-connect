"""Private HTTP API for rules-based security scanning."""

import hmac
import logging
from typing import Annotated

from fastapi import Depends, FastAPI, Header, HTTPException, status

from openconnect_security.config import Settings
from openconnect_security.detectors.rules import scan as scan_text
from openconnect_security.schemas import ScanRequest, ScanResponse

logger = logging.getLogger("openconnect_security")
app = FastAPI(title="OpenConnect Security Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()


@app.get("/health/live")
def live() -> dict[str, str]:
    return {"status": "live"}


@app.get("/health/ready")
def ready() -> dict[str, object]:
    # Rules detector is initialized at import; optional models are not advertised.
    return {"status": "ready", "capabilities": ["rules"]}


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
    # Fail closed for requested detectors that this rules-only worker does not provide.
    if any(detector != "rules" for detector in request.detectors):
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
        detectorVersion="1.0.0",
        spans=spans,
    )
