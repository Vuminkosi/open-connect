"""CPU-first PaddleOCR detector adapter with strict lazy imports and text-PII mapping."""

from __future__ import annotations

import math
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from openconnect_security.detectors.base import DetectorLifecycleState, DetectorStatusSnapshot
from openconnect_security.detectors.rules import scan as scan_rules
from openconnect_security.media import ValidatedImage
from openconnect_security.runtime_cache import (
    AssetFetcher,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)
from openconnect_security.schemas import ImageRegionCategory, ImageRegionFinding, TextSpanFinding

_VALID_IMAGE_CATEGORIES: frozenset[str] = frozenset(
    {
        "FACE",
        "TEXT_PII",
        "PERSON",
        "EMAIL",
        "PHONE",
        "ADDRESS",
        "DATE_OF_BIRTH",
        "GOVERNMENT_ID",
        "ACCOUNT_NUMBER",
        "PAYMENT_CARD",
        "SECRET",
        "CUSTOM",
    }
)


@dataclass(frozen=True)
class OcrLineBox:
    text: str
    x: int
    y: int
    width: int
    height: int
    confidence: float


OcrBackend = Callable[[ValidatedImage], list[OcrLineBox]]
SemanticTextScanner = Callable[[str], list[TextSpanFinding]]


class PaddleOcrDetector:
    """Lazy-loading local OCR detector mapping PII in OCR lines to image redaction boxes."""

    detector_id = "paddle-ocr"
    modalities = ("image",)

    def __init__(
        self,
        *,
        manifest: RuntimeModelManifest | None = None,
        min_confidence: float = 0.5,
        backend: OcrBackend | None = None,
    ) -> None:
        self._manifest = manifest
        self._min_confidence = min_confidence
        self._backend = backend
        self._state: DetectorLifecycleState = "disabled"
        self._reason_code: str | None = "detector_disabled"
        self._version: str | None = None
        self._revision_dir: Path | None = None

    def status(self) -> DetectorStatusSnapshot:
        return DetectorStatusSnapshot(
            detector_id=self.detector_id,
            state=self._state,
            version=self._version,
            model_id=self._manifest.modelId if self._manifest else None,
            revision=self._manifest.immutableRevision if self._manifest else None,
            reason_code=self._reason_code,
        )

    def is_ready(self) -> bool:
        return self._state == "ready" and self._backend is not None

    def initialize(
        self,
        cache_manager: RuntimeModelCacheManager,
        *,
        enabled: bool,
        provisioning: str = "first-start-if-missing",
        offline: bool = False,
        license_accepted: bool = False,
        asset_fetcher: AssetFetcher | None = None,
    ) -> DetectorStatusSnapshot:
        if not enabled:
            self._state = "disabled"
            self._reason_code = "detector_disabled"
            self._version = None
            return self.status()

        if self._backend is not None:
            self._state = "ready"
            self._reason_code = None
            self._version = self._manifest.immutableRevision if self._manifest else "injected"
            return self.status()

        self._state = "provisioning"
        result = cache_manager.provision_or_verify(
            self._manifest,
            enabled=True,
            provisioning=provisioning,
            offline=offline,
            license_accepted=license_accepted,
            asset_fetcher=asset_fetcher,
        )
        if not result.ready or result.revision_dir is None:
            self._state = "unavailable"
            self._reason_code = result.reason or "model_unavailable"
            self._version = None
            return self.status()

        assets_dir = result.revision_dir / "assets"
        try:
            # Strict lazy import: never imported unless enabled and cache is verified.
            import numpy as np  # type: ignore[import-not-found]
            from paddleocr import PaddleOCR  # type: ignore[import-not-found]

            ocr = PaddleOCR(
                use_angle_cls=False,
                lang="en",
                use_gpu=False,
                show_log=False,
                det_model_dir=str(assets_dir),
                rec_model_dir=str(assets_dir),
            )

            def _run(validated: ValidatedImage) -> list[OcrLineBox]:
                arr = np.array(validated.image.convert("RGB"))
                raw = ocr.ocr(arr, cls=False)
                boxes: list[OcrLineBox] = []
                if not raw:
                    return boxes
                for page in raw:
                    if not page:
                        continue
                    for entry in page:
                        polygon, (line_text, conf) = entry
                        xs = [round(pt[0]) for pt in polygon]
                        ys = [round(pt[1]) for pt in polygon]
                        x0 = max(0, min(xs))
                        y0 = max(0, min(ys))
                        x1 = min(validated.width, max(xs))
                        y1 = min(validated.height, max(ys))
                        if x1 > x0 and y1 > y0:
                            boxes.append(
                                OcrLineBox(
                                    text=str(line_text),
                                    x=x0,
                                    y=y0,
                                    width=x1 - x0,
                                    height=y1 - y0,
                                    confidence=float(conf),
                                )
                            )
                return boxes

            self._backend = _run
            self._revision_dir = result.revision_dir
            self._version = result.revision or "verified"
            self._state = "ready"
            self._reason_code = None
        except ImportError:
            self._state = "unavailable"
            self._reason_code = "runtime_dependency_missing"
            self._version = None
        except Exception:  # noqa: BLE001 - Fail closed on any model load failure.
            self._state = "unavailable"
            self._reason_code = "model_load_failed"
            self._version = None
        return self.status()

    def detect(
        self,
        validated: ValidatedImage,
        *,
        semantic_scanner: SemanticTextScanner | None = None,
    ) -> list[ImageRegionFinding]:
        if not self.is_ready() or self._backend is None:
            raise RuntimeError("detector_unavailable:paddle-ocr")

        lines = self._backend(validated)
        findings: list[ImageRegionFinding] = []
        for line in lines:
            if not math.isfinite(line.confidence) or not (0.0 <= line.confidence <= 1.0):
                raise ValueError("invalid_ocr_confidence")
            if line.confidence < self._min_confidence:
                continue
            if (
                line.x < 0
                or line.y < 0
                or line.width < 1
                or line.height < 1
                or line.x + line.width > validated.width
                or line.y + line.height > validated.height
            ):
                raise ValueError("ocr_region_out_of_bounds")

            # Run rules (and optional semantic GLiNER) on extracted line text.
            # If PII is found anywhere in the line, redact the entire line box conservatively.
            rule_spans = scan_rules(line.text)
            categories: list[str] = [s.entityType for s in rule_spans]
            if semantic_scanner is not None and line.text.strip():
                semantic_spans = semantic_scanner(line.text)
                categories.extend(s.category for s in semantic_spans)

            if not categories:
                continue

            primary = categories[0]
            category: ImageRegionCategory = (
                primary if primary in _VALID_IMAGE_CATEGORIES else "TEXT_PII"  # type: ignore[assignment]
            )
            findings.append(
                ImageRegionFinding(
                    detectorId="paddle-ocr",
                    category=category,
                    x=line.x,
                    y=line.y,
                    width=line.width,
                    height=line.height,
                    confidence=line.confidence,
                    recommendedAction="redact",
                )
            )
        return findings
