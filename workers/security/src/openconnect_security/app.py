"""Private HTTP API for security scanning (rules, malware, and optional model-backed detectors)."""

from __future__ import annotations

import hmac
import logging
import time
from collections.abc import Callable
from typing import Annotated
from uuid import UUID

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, UploadFile, status

from openconnect_security import engine_mode
from openconnect_security.config import DETECTOR_CAPABILITY_ID_MAP, Settings
from openconnect_security.detectors.rules import scan as scan_text
from openconnect_security.malware import MALWARE_DETECTOR_VERSION, SecurityMalwareInspector
from openconnect_security.media import (
    MAX_FILE_BYTES,
    MAX_IMAGE_BYTES,
    MediaLimitExceededError,
    UnsupportedMediaError,
    ValidatedImage,
    sha256_bytes,
    sniff_mime,
    validate_and_normalize_image,
)
from openconnect_security.schemas import (
    CapabilitiesResponse,
    CapabilityDescriptor,
    DetectorExecutionStatus,
    ImageMetadata,
    ImageRegionFinding,
    InjectionFinding,
    InspectionFinding,
    InspectionRequest,
    InspectionResponse,
    MalwareFinding,
    ScanRequest,
    ScanResponse,
    TextSpanFinding,
)

logger = logging.getLogger("openconnect_security")
app = FastAPI(title="OpenConnect Security Worker", docs_url=None, redoc_url=None)
settings = Settings.from_environment()
RULES_DETECTOR_VERSION = "1.1.0"

# Injectable detector hooks for model-backed detectors (wired in Phase 2 / tests).
# Each entry maps detector_id -> (version, callable).
_text_detectors: dict[str, tuple[str, Callable[[str], list[TextSpanFinding]]]] = {}
_prompt_detectors: dict[str, tuple[str, Callable[[str], list[InjectionFinding]]]] = {}
_image_detectors: dict[str, tuple[str, Callable[[ValidatedImage], list[ImageRegionFinding]]]] = {}
_malware_inspector_override: SecurityMalwareInspector | None = None

_TEXT_ALLOWED_DETECTORS = frozenset({"rules", "gliner-pii"})
_PROMPT_ALLOWED_DETECTORS = frozenset({"deberta-injection", "qwen3guard"})
_IMAGE_ALLOWED_DETECTORS = frozenset({"paddle-ocr", "yunet"})


def _get_malware_inspector() -> SecurityMalwareInspector:
    if _malware_inspector_override is not None:
        return _malware_inspector_override
    return SecurityMalwareInspector(
        settings.clamav_socket_path,
        timeout_seconds=settings.clamav_timeout_seconds,
    )


def _detector_version(detector_id: str) -> str | None:
    if detector_id == "rules" and settings.rules_enabled:
        return RULES_DETECTOR_VERSION
    if detector_id == "malware" and settings.malware_enabled:
        return MALWARE_DETECTOR_VERSION if _get_malware_inspector().is_ready() else None
    if detector_id in _text_detectors and detector_id in settings.enabled_detectors():
        return _text_detectors[detector_id][0]
    if detector_id in _prompt_detectors and detector_id in settings.enabled_detectors():
        return _prompt_detectors[detector_id][0]
    if detector_id in _image_detectors and detector_id in settings.enabled_detectors():
        return _image_detectors[detector_id][0]
    return None


def _ready_detectors() -> list[tuple[str, str]]:
    ready_list: list[tuple[str, str]] = []
    for detector_id in settings.enabled_detectors():
        version = _detector_version(detector_id)
        if version is not None:
            ready_list.append((detector_id, version))
    return ready_list


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
    ready_pairs = _ready_detectors()
    ready_ids = [det_id for det_id, _ in ready_pairs]
    ready_set = set(ready_ids)

    missing_models = [d for d in settings.enabled_model_detectors() if d not in ready_set]
    if missing_models:
        return {
            "status": "unready",
            "ready": False,
            "reason": "detector_not_implemented",
            "missingDetectors": missing_models,
            "capabilities": ready_ids,
        }
    if settings.malware_enabled and "malware" not in ready_set:
        return {
            "status": "unready",
            "ready": False,
            "reason": "malware_scanner_unavailable",
            "missingDetectors": ["malware"],
            "capabilities": ready_ids,
        }
    if not settings.fake_engine and enabled == ["rules"]:
        # Rules-only default profile: preserve exact compact readiness contract.
        return {"status": "ready", "capabilities": ["rules"]}
    return {
        "status": "ready",
        "ready": True,
        "engine": "fake" if settings.fake_engine else "real",
        "capabilities": ready_ids,
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


def _enforce_engine_mode() -> None:
    _, status_code, code = engine_mode.rejection(settings.fake_engine, settings.model_device)
    if code is not None:
        raise HTTPException(
            status_code=status_code or status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=code,
        )


@app.get(
    "/internal/v1/capabilities",
    response_model=CapabilitiesResponse,
    dependencies=[Depends(authenticate)],
)
def capabilities() -> CapabilitiesResponse:
    """Return only verified ready capabilities matching Host WorkerCapabilityResponseParser."""
    _enforce_engine_mode()
    descriptors = [
        CapabilityDescriptor(
            schemaVersion="openconnect.v1",
            id=DETECTOR_CAPABILITY_ID_MAP[det_id],
            kind="security",
            state="ready",
            version=version,
            localOnly=True,
        )
        for det_id, version in _ready_detectors()
        if det_id in DETECTOR_CAPABILITY_ID_MAP
    ]
    return CapabilitiesResponse(capabilities=descriptors)


@app.post("/scan", response_model=ScanResponse, dependencies=[Depends(authenticate)])
def scan(request: ScanRequest) -> ScanResponse:
    _enforce_engine_mode()
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


@app.post(
    "/internal/v1/inspect/text",
    response_model=InspectionResponse,
    dependencies=[Depends(authenticate)],
)
def inspect_text(request: InspectionRequest) -> InspectionResponse:
    _enforce_engine_mode()
    if request.modality != "text" or request.text is None:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="modality must be 'text' with non-null text",
        )
    invalid = [d for d in request.detectors if d not in _TEXT_ALLOWED_DETECTORS]
    if invalid:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="unsupported detector for text modality",
        )

    started = time.perf_counter()
    text = request.text
    text_len = len(text)
    detector_statuses: list[DetectorExecutionStatus] = []
    findings: list[InspectionFinding] = []

    for detector_id in request.detectors:
        version = _detector_version(detector_id)
        if version is None:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail=f"detector_unavailable:{detector_id}",
            )
        try:
            if detector_id == "rules":
                spans = scan_text(text)
                for span in spans:
                    if span.start < 0 or span.end > text_len or span.start >= span.end:
                        raise ValueError("invalid_span_offsets")
                    findings.append(
                        TextSpanFinding(
                            detectorId="rules",
                            category=span.entityType,
                            start=span.start,
                            end=span.end,
                            confidence=span.confidence,
                            recommendedAction="redact" if span.entityType == "SECRET" else span.decision,
                        )
                    )
            else:
                _, runner = _text_detectors[detector_id]
                det_findings = runner(text)
                for item in det_findings:
                    if item.start < 0 or item.end > text_len or item.start >= item.end:
                        raise ValueError("invalid_span_offsets")
                    findings.append(item)
            detector_statuses.append(
                DetectorExecutionStatus(id=detector_id, version=version, status="completed")
            )
        except HTTPException:
            raise
        except Exception:
            logger.exception("Text inspection failed request_id=%s detector=%s", request.requestId, detector_id)
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="inspection_failed",
            ) from None

    duration_ms = int((time.perf_counter() - started) * 1000)
    return InspectionResponse(
        requestId=request.requestId,
        modality="text",
        status="completed",
        detectors=detector_statuses,
        findings=findings,
        offsetUnit="unicode-scalar",
        durationMs=duration_ms,
    )


@app.post(
    "/internal/v1/inspect/prompt",
    response_model=InspectionResponse,
    dependencies=[Depends(authenticate)],
)
def inspect_prompt(request: InspectionRequest) -> InspectionResponse:
    _enforce_engine_mode()
    if request.modality != "prompt" or request.text is None:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="modality must be 'prompt' with non-null text",
        )
    invalid = [d for d in request.detectors if d not in _PROMPT_ALLOWED_DETECTORS]
    if invalid:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="unsupported detector for prompt modality",
        )

    started = time.perf_counter()
    detector_statuses: list[DetectorExecutionStatus] = []
    findings: list[InspectionFinding] = []

    for detector_id in request.detectors:
        version = _detector_version(detector_id)
        if version is None:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail=f"detector_unavailable:{detector_id}",
            )
        try:
            _, runner = _prompt_detectors[detector_id]
            findings.extend(runner(request.text))
            detector_statuses.append(
                DetectorExecutionStatus(id=detector_id, version=version, status="completed")
            )
        except Exception:
            logger.exception("Prompt inspection failed request_id=%s detector=%s", request.requestId, detector_id)
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="inspection_failed",
            ) from None

    duration_ms = int((time.perf_counter() - started) * 1000)
    return InspectionResponse(
        requestId=request.requestId,
        modality="prompt",
        status="completed",
        detectors=detector_statuses,
        findings=findings,
        offsetUnit="unicode-scalar",
        durationMs=duration_ms,
    )


async def _read_bounded_upload(upload: UploadFile, max_bytes: int) -> tuple[bytes, bool]:
    chunks = bytearray()
    while True:
        chunk = await upload.read(64 * 1024)
        if not chunk:
            break
        chunks.extend(chunk)
        if len(chunks) > max_bytes:
            return bytes(chunks[:max_bytes]), True
    return bytes(chunks), False


def _verify_expected_sha256(actual: str, expected: str | None) -> None:
    if expected is None:
        return
    normalized = expected.strip().lower()
    if len(normalized) != 64 or any(c not in "0123456789abcdef" for c in normalized):
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="invalid_expected_sha256",
        )
    if not hmac.compare_digest(actual, normalized):
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="digest_mismatch",
        )


@app.post(
    "/internal/v1/inspect/file",
    response_model=InspectionResponse,
    dependencies=[Depends(authenticate)],
)
async def inspect_file(
    file: Annotated[UploadFile, File(...)],
    requestId: Annotated[UUID, Form(...)],
    policyRevision: Annotated[str | None, Form()] = None,
    expectedSha256: Annotated[str | None, Form()] = None,
) -> InspectionResponse:
    del policyRevision  # Bound by host policy snapshot; echoed in evidence by host.
    _enforce_engine_mode()
    if not settings.malware_enabled:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="detector_unavailable:malware",
        )

    started = time.perf_counter()
    data, exceeded = await _read_bounded_upload(file, MAX_FILE_BYTES)
    if exceeded:
        duration_ms = int((time.perf_counter() - started) * 1000)
        return InspectionResponse(
            requestId=requestId,
            modality="file",
            status="limit_exceeded",
            detectors=[
                DetectorExecutionStatus(
                    id="malware",
                    version=MALWARE_DETECTOR_VERSION,
                    status="limit_exceeded",
                    reasonCode="file_size_exceeded",
                )
            ],
            findings=[
                MalwareFinding(
                    outcome="limit_exceeded",
                    reasonCode="file_size_exceeded",
                )
            ],
            durationMs=duration_ms,
        )

    if not data:
        duration_ms = int((time.perf_counter() - started) * 1000)
        return InspectionResponse(
            requestId=requestId,
            modality="file",
            status="unsupported",
            detectors=[
                DetectorExecutionStatus(
                    id="malware",
                    version=MALWARE_DETECTOR_VERSION,
                    status="unsupported",
                    reasonCode="empty_file",
                )
            ],
            findings=[MalwareFinding(outcome="unsupported", reasonCode="empty_file")],
            durationMs=duration_ms,
        )

    digest = sha256_bytes(data)
    _verify_expected_sha256(digest, expectedSha256)
    mime = sniff_mime(data)
    outcome = _get_malware_inspector().scan_bytes(data)
    duration_ms = int((time.perf_counter() - started) * 1000)

    if outcome.outcome in ("clean", "detected"):
        overall_status = "completed"
        det_status = "completed"
    elif outcome.outcome == "limit_exceeded":
        overall_status = "limit_exceeded"
        det_status = "limit_exceeded"
    elif outcome.outcome == "unsupported":
        overall_status = "unsupported"
        det_status = "unsupported"
    else:
        overall_status = "unavailable"
        det_status = "unavailable"

    return InspectionResponse(
        requestId=requestId,
        modality="file",
        status=overall_status,
        detectors=[
            DetectorExecutionStatus(
                id="malware",
                version=MALWARE_DETECTOR_VERSION,
                status=det_status,
                reasonCode=None if det_status == "completed" else outcome.reason_code,
            )
        ],
        findings=[
            MalwareFinding(
                outcome=outcome.outcome,
                reasonCode=outcome.reason_code,
                signatureVersion=outcome.signature_version,
            )
        ],
        contentSha256=digest,
        contentType=mime,
        durationMs=duration_ms,
    )


@app.post(
    "/internal/v1/inspect/image",
    response_model=InspectionResponse,
    dependencies=[Depends(authenticate)],
)
async def inspect_image(
    file: Annotated[UploadFile, File(...)],
    requestId: Annotated[UUID, Form(...)],
    detectors: Annotated[str, Form()] = "paddle-ocr,yunet",
    policyRevision: Annotated[str | None, Form()] = None,
    expectedSha256: Annotated[str | None, Form()] = None,
) -> InspectionResponse:
    del policyRevision
    _enforce_engine_mode()
    requested = [item.strip() for item in detectors.split(",") if item.strip()]
    if not requested or len(set(requested)) != len(requested):
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="detectors must be a non-empty list of unique image detector IDs",
        )
    if any(d not in _IMAGE_ALLOWED_DETECTORS for d in requested):
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="unsupported detector for image modality",
        )

    started = time.perf_counter()
    data, exceeded = await _read_bounded_upload(file, MAX_IMAGE_BYTES)
    if exceeded:
        duration_ms = int((time.perf_counter() - started) * 1000)
        return InspectionResponse(
            requestId=requestId,
            modality="image",
            status="limit_exceeded",
            detectors=[
                DetectorExecutionStatus(
                    id=det_id,  # type: ignore[arg-type]
                    version=_detector_version(det_id) or "unready",
                    status="limit_exceeded",
                    reasonCode="image_bytes_limit_exceeded",
                )
                for det_id in requested
            ],
            findings=[],
            durationMs=duration_ms,
        )

    try:
        validated = validate_and_normalize_image(data)
    except MediaLimitExceededError as exc:
        duration_ms = int((time.perf_counter() - started) * 1000)
        return InspectionResponse(
            requestId=requestId,
            modality="image",
            status="limit_exceeded",
            detectors=[
                DetectorExecutionStatus(
                    id=det_id,  # type: ignore[arg-type]
                    version=_detector_version(det_id) or "unready",
                    status="limit_exceeded",
                    reasonCode=str(exc),
                )
                for det_id in requested
            ],
            findings=[],
            durationMs=duration_ms,
        )
    except UnsupportedMediaError as exc:
        duration_ms = int((time.perf_counter() - started) * 1000)
        return InspectionResponse(
            requestId=requestId,
            modality="image",
            status="unsupported",
            detectors=[
                DetectorExecutionStatus(
                    id=det_id,  # type: ignore[arg-type]
                    version=_detector_version(det_id) or "unready",
                    status="unsupported",
                    reasonCode=str(exc),
                )
                for det_id in requested
            ],
            findings=[],
            durationMs=duration_ms,
        )

    _verify_expected_sha256(validated.sha256, expectedSha256)

    detector_statuses: list[DetectorExecutionStatus] = []
    findings: list[InspectionFinding] = []
    for det_id in requested:
        version = _detector_version(det_id)
        if version is None:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail=f"detector_unavailable:{det_id}",
            )
        try:
            _, runner = _image_detectors[det_id]
            regions = runner(validated)
            for region in regions:
                if (
                    region.x + region.width > validated.width
                    or region.y + region.height > validated.height
                ):
                    raise ValueError("image_region_out_of_bounds")
                findings.append(region)
            detector_statuses.append(
                DetectorExecutionStatus(
                    id=det_id,  # type: ignore[arg-type]
                    version=version,
                    status="completed",
                )
            )
        except Exception:
            logger.exception("Image inspection failed request_id=%s detector=%s", requestId, det_id)
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="inspection_failed",
            ) from None

    duration_ms = int((time.perf_counter() - started) * 1000)
    return InspectionResponse(
        requestId=requestId,
        modality="image",
        status="completed",
        detectors=detector_statuses,
        findings=findings,
        contentSha256=validated.sha256,
        contentType=validated.content_type,
        imageMetadata=ImageMetadata(
            width=validated.width,
            height=validated.height,
            coordinateSpace="top-left-pixels",
            orientationNormalized=validated.orientation_normalized,
        ),
        durationMs=duration_ms,
    )
